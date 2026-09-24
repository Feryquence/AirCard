import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch

from PIL import Image
from pypdf import PdfWriter

SCRIPT = Path(__file__).resolve().parents[1] / '.github/scripts/card_submissions.py'
spec = importlib.util.spec_from_file_location('card_submissions', SCRIPT)
cards = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cards)

URL = 'https://github.com/user-attachments/assets/12345678-1234-1234-1234-123456789abc'


def form(kind='png', fanmade='false', description='_No response_'):
    values = ['suica', 'Test uploader', description, fanmade, kind, '![card](' + URL + ')']
    return '\n\n'.join('### ' + heading + '\n\n' + value for heading, value in zip(cards.HEADERS, values))


def event(body=None):
    return {'action': 'labeled', 'label': {'name': cards.APPROVAL},
            'repository': {'full_name': cards.REPOSITORY}, 'sender': {'login': 'maintainer'},
            'issue': {'number': 12, 'state': 'open', 'body': body or form(),
                      'labels': [{'name': cards.APPROVAL}, {'name': cards.SUBMISSION}]}}


def picture(fmt='PNG'):
    stream = io.BytesIO()
    Image.new('RGB', (16, 10), '#4080c0').save(stream, format=fmt)
    return stream.getvalue()


def pdf(pages=1, encrypted=False):
    writer = PdfWriter()
    for _ in range(pages):
        writer.add_blank_page(width=200, height=100)
    if encrypted:
        writer.encrypt('password')
    stream = io.BytesIO()
    writer.write(stream)
    return stream.getvalue()


class FakeGitHub:
    def __init__(self, submitted=None):
        self.issue = copy.deepcopy((submitted or event())['issue'])
        self.permission = 'write'
        self.head = 'start'
        self.trees = {'empty': {}}
        self.commits = {'start': {'tree': {'sha': 'empty'}}}
        self.blobs = {}
        self.calls = []
        self.conflict = False
        self.edit_before_publish = False

    def blob(self, data):
        sha = hashlib.sha256(data).hexdigest()
        self.blobs[sha] = data
        return sha

    def read_json(self, path, commit):
        files = self.trees[self.commits[commit]['tree']['sha']]
        return json.loads(self.blobs[files[path]]) if path in files else None

    def api(self, path, method='GET', body=None):
        self.calls.append((path, method, copy.deepcopy(body)))
        if path.startswith('/collaborators/'):
            return {'permission': self.permission}
        if path == '/issues/12':
            return copy.deepcopy(self.issue)
        if path == '/git/ref/heads/cards':
            return {'object': {'sha': self.head}}
        if path.startswith('/git/commits/') and method == 'GET':
            return copy.deepcopy(self.commits[path.rsplit('/', 1)[1]])
        if path == '/git/trees' and method == 'POST':
            tree = dict(self.trees[body['base_tree']])
            for node in body['tree']:
                self.assert_safe_node(node)
                tree[node['path']] = node['sha']
            sha = 'tree' + str(len(self.trees))
            self.trees[sha] = tree
            return {'sha': sha}
        if path == '/git/commits' and method == 'POST':
            sha = 'commit' + str(len(self.commits))
            self.commits[sha] = {'tree': {'sha': body['tree']}, 'parents': body['parents']}
            if self.edit_before_publish:
                self.issue['body'] += '\nchanged after approval'
            return {'sha': sha}
        if path == '/git/refs/heads/cards' and method == 'PATCH':
            if body['force']:
                raise AssertionError('force push forbidden')
            if self.conflict:
                self.conflict = False
                other = {'name': 'other', 'uploader': 'other', 'description': '', 'fanmade': True,
                         'type': 'png', 'source': 'https://example.invalid/other.png'}
                self.trees['concurrent-tree'] = {'cards.json': self.blob(cards.json_bytes({'cards': [other]}))}
                self.commits['concurrent'] = {'tree': {'sha': 'concurrent-tree'}}
                self.head = 'concurrent'
                raise cards.ApiError(422)
            if self.commits[body['sha']]['parents'] != [self.head]:
                raise AssertionError('stale parent')
            self.head = body['sha']
            return {'object': {'sha': self.head}}
        raise AssertionError('Unexpected API request: ' + method + ' ' + path)

    @staticmethod
    def assert_safe_node(node):
        assert node['mode'] == '100644' and node['type'] == 'blob'
        assert node['path'] == 'cards.json' or node['path'].startswith(('files/', '.submissions/'))


class CardSubmissionTests(unittest.TestCase):
    def test_form_matches_user_schema(self):
        metadata, url = cards.parse_submission(form())
        self.assertEqual(metadata, {'name': 'suica', 'uploader': 'Test uploader', 'description': '', 'fanmade': False, 'type': 'png'})
        self.assertEqual(url, URL)
        self.assertTrue(cards.parse_submission(form(fanmade='true', description='custom'))[0]['fanmade'])

    def test_reject_malformed_fields_and_extra_attachments(self):
        for text in [form(fanmade='yes'), form(kind='exe'), form() + '\n' + URL,
                     form() + '\n\n### 卡面名称（name）\n\nother', form().replace('suica', ''),
                     form().replace('suica', 'x' * 101), form(description='a' * 2001)]:
            with self.subTest(text=text[:25]), self.assertRaises(cards.SubmissionError):
                cards.parse_submission(text)

    def test_attachment_url_allowlist(self):
        cards.validate_attachment_url(URL, True)
        cards.validate_attachment_url('https://github.com/user-attachments/files/123/card.pdf', True)
        cards.validate_attachment_url('https://github-production-user-asset-1.githubuserContent.com/x?signature=a')
        for url in ['https://example.com/card.png', 'http://github.com/user-attachments/assets/a',
                    'https://github.com.evil.test/x', 'https://127.0.0.1/x',
                    'https://github.com:444/user-attachments/files/1/card.pdf',
                    'https://token@github.com/user-attachments/files/1/card.pdf',
                    URL + '?x=y', URL + '#x', 'file:///private/file',
                    'https://github.com/Feryquence/AirCard/raw/main/App.xaml']:
            with self.subTest(url=url), self.assertRaises(cards.SubmissionError):
                cards.validate_attachment_url(url, True)

    def test_file_validation(self):
        for kind, data in [('png', picture()), ('jpg', picture('JPEG')), ('pdf', pdf())]:
            cards.validate_file(data, kind)
        for kind, data in [('png', picture('JPEG')), ('pdf', picture()), ('pdf', pdf(2)),
                           ('pdf', pdf(encrypted=True)), ('jpg', b'not an image'), ('png', picture()[:35])]:
            with self.subTest(kind=kind), self.assertRaises(cards.SubmissionError):
                cards.validate_file(data, kind)

    def test_untrusted_or_changed_approval_cannot_write(self):
        for change in ['permission', 'body', 'labels', 'closed', 'pr']:
            submitted = event()
            api = FakeGitHub(submitted)
            if change == 'permission': api.permission = 'read'
            if change == 'body': api.issue['body'] += 'edit'
            if change == 'labels': api.issue['labels'] = []
            if change == 'closed': api.issue['state'] = 'closed'
            if change == 'pr': submitted['issue']['pull_request'] = {}
            with self.subTest(change=change), self.assertRaises(cards.SubmissionError):
                cards.verify_approval(api, submitted)
            self.assertFalse(any(method != 'GET' for _, method, _ in api.calls))

    def test_atomic_write_exact_schema_and_idempotence(self):
        submitted = event()
        api = FakeGitHub(submitted)
        metadata, _ = cards.parse_submission(submitted['issue']['body'])
        data = picture()
        source, created = cards.publish(api, submitted, metadata, data)
        self.assertTrue(created)
        self.assertTrue(source.startswith('https://raw.githubusercontent.com/Feryquence/AirCard/cards/files/'))
        catalog = api.read_json('cards.json', api.head)
        self.assertEqual(catalog, {'cards': [dict(metadata, source=source)]})
        files = api.trees[api.commits[api.head]['tree']['sha']]
        self.assertEqual(api.blobs[files['files/' + hashlib.sha256(data).hexdigest() + '.png']], data)
        old_head = api.head
        self.assertEqual(cards.publish(api, submitted, metadata, data), (source, False))
        self.assertEqual(api.head, old_head)
        self.assertFalse(any('/refs/heads/main' in path for path, _, _ in api.calls))

    def test_concurrent_submission_is_preserved(self):
        api = FakeGitHub()
        api.conflict = True
        cards.publish(api, event(), cards.parse_submission(form())[0], picture())
        self.assertEqual([c['name'] for c in api.read_json('cards.json', api.head)['cards']], ['other', 'suica'])

    def test_late_edit_does_not_advance_branch(self):
        api = FakeGitHub()
        api.edit_before_publish = True
        with self.assertRaises(cards.SubmissionError):
            cards.publish(api, event(), cards.parse_submission(form())[0], picture())
        self.assertEqual(api.head, 'start')

    def test_invalid_existing_index_is_not_overwritten(self):
        for broken in [{}, {'cards': 'invalid'}, {'cards': [{'name': 'missing fields'}]}]:
            api = FakeGitHub()
            api.trees['empty']['cards.json'] = api.blob(cards.json_bytes(broken))
            with self.subTest(broken=broken), self.assertRaises(cards.SubmissionError):
                cards.publish(api, event(), cards.parse_submission(form())[0], picture())
            self.assertEqual(api.head, 'start')

    def test_token_is_not_attached_to_downloads(self):
        class Response(io.BytesIO):
            headers = {}
        class Opener:
            def open(self, request, timeout):
                self.request = request
                return Response(b'raw bytes')
        opener = Opener()
        with patch.object(cards.urllib.request, 'build_opener', return_value=opener):
            self.assertEqual(cards.download_attachment(URL), b'raw bytes')
        self.assertIsNone(opener.request.get_header('Authorization'))


if __name__ == '__main__':
    unittest.main()
