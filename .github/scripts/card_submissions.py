"""Publish a maintainer-approved issue attachment to the cards branch only."""
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import warnings

from PIL import Image
from pypdf import PdfReader

REPOSITORY = 'Feryquence/AirCard'
BRANCH = 'cards'
APPROVAL = '审核通过'
SUBMISSION = '卡面投稿'
HEADERS = {
    '卡面名称（name）': 'name', '上传者（uploader）': 'uploader',
    '描述（description）': 'description', '是否二创（fanmade）': 'fanmade',
    '文件类型（type）': 'type', '卡面文件': 'attachment',
}
FIELDS = {'name', 'uploader', 'description', 'fanmade', 'type', 'source'}
MAX_BYTES = 25 * 1024 * 1024
MAX_JSON = 2 * 1024 * 1024


class SubmissionError(Exception):
    pass


class ApiError(SubmissionError):
    def __init__(self, status):
        self.status = status
        super().__init__('GitHub API returned HTTP ' + str(status))


def require(condition, message):
    if not condition:
        raise SubmissionError(message)


def parse_submission(body):
    require(isinstance(body, str) and len(body) <= 20000, '投稿内容为空或过长。')
    sections = {}
    headings = list(re.finditer(r'^### ([^\r\n]+)\r?$', body, re.MULTILINE))
    for i, match in enumerate(headings):
        label = match.group(1)
        require(label in HEADERS, '投稿包含未知标题，请使用卡面投稿表单。')
        key = HEADERS[label]
        require(key not in sections, '投稿包含重复字段。')
        value = body[match.end():headings[i + 1].start() if i + 1 < len(headings) else len(body)].strip()
        sections[key] = '' if value == '_No response_' else value
    require(set(sections) == set(HEADERS.values()), '投稿字段不完整，请使用卡面投稿表单。')
    for key, limit in [('name', 100), ('uploader', 100)]:
        value = sections[key]
        require(0 < len(value) <= limit and not any(ord(c) < 32 for c in value), key + ' 不能为空、换行或超过 100 字符。')
    require(len(sections['description']) <= 2000, '描述不能超过 2000 字符。')
    require(sections['fanmade'] in ('true', 'false'), 'fanmade 必须选择 true 或 false。')
    require(sections['type'] in ('png', 'jpg', 'pdf'), '只接受 png、jpg 或 pdf。')
    links = re.findall(r'https?://[^\s<>"\)]+', sections['attachment'])
    require(len(links) == 1, '每条投稿只能上传一个 GitHub 附件。')
    validate_attachment_url(links[0], initial=True)
    return {
        'name': sections['name'], 'uploader': sections['uploader'],
        'description': sections['description'], 'fanmade': sections['fanmade'] == 'true',
        'type': sections['type'],
    }, links[0]


def validate_attachment_url(url, initial=False):
    parsed = urllib.parse.urlsplit(url)
    try:
        port = parsed.port
    except ValueError:
        raise SubmissionError('附件地址端口无效。') from None
    require(parsed.scheme == 'https' and port in (None, 443) and not parsed.username and not parsed.password and not parsed.fragment, '附件必须是 GitHub HTTPS 上传地址。')
    host = parsed.hostname or ''
    if initial or host == 'github.com':
        require(host == 'github.com' and re.fullmatch(r'/user-attachments/(?:assets/[a-fA-F0-9-]{36}|files/[0-9]+/[^/]+)', parsed.path), '请直接在表单中上传附件，不要使用外部图片链接。')
        require(not parsed.query, '附件初始地址不应包含查询参数。')
    else:
        require(host.endswith('.githubusercontent.com'), '附件跳转到了不受支持的服务器。')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def download_attachment(url):
    opener = urllib.request.build_opener(NoRedirect())
    for hop in range(6):
        validate_attachment_url(url, initial=(hop == 0))
        # Deliberately separate from the authenticated API client: no token is
        # ever sent to attachments or their redirect destinations.
        request = urllib.request.Request(url, headers={'User-Agent': 'AirCard-Submissions'})
        try:
            with opener.open(request, timeout=30) as response:
                declared = response.headers.get('Content-Length')
                require(declared is None or (declared.isdigit() and 0 < int(declared) <= MAX_BYTES), '附件大小不符合要求。')
                data = response.read(MAX_BYTES + 1)
                require(0 < len(data) <= MAX_BYTES, '附件为空或超过 25 MiB。')
                require(declared is None or len(data) == int(declared), '附件下载不完整。')
                return data
        except urllib.error.HTTPError as error:
            destination = error.headers.get('Location')
            error.close()
            if error.code in (301, 302, 303, 307, 308) and destination:
                url = urllib.parse.urljoin(url, destination)
                continue
            raise SubmissionError('下载 GitHub 附件失败（HTTP ' + str(error.code) + '）。') from None
        except (urllib.error.URLError, TimeoutError):
            raise SubmissionError('下载 GitHub 附件失败，请稍后重试。') from None
    raise SubmissionError('附件重定向次数过多。')


def validate_file(data, kind):
    require(isinstance(data, bytes) and 0 < len(data) <= MAX_BYTES, '附件大小不符合要求。')
    require(kind in ('png', 'jpg', 'pdf'), '不支持的文件类型。')
    try:
        if kind == 'pdf':
            require(data.startswith(b'%PDF-'), '文件内容与选择的 PDF 类型不符。')
            pdf = PdfReader(io.BytesIO(data), strict=True)
            require(not pdf.is_encrypted and len(pdf.pages) == 1, 'PDF 必须未加密且只有一页。')
            page = pdf.pages[0]
            require(0 < float(page.mediabox.width) <= 14400 and 0 < float(page.mediabox.height) <= 14400, 'PDF 页面尺寸无效。')
        else:
            require(len(data) <= 10 * 1024 * 1024, 'PNG/JPG 不能超过 10 MiB。')
            with warnings.catch_warnings():
                warnings.simplefilter('error', Image.DecompressionBombWarning)
                with Image.open(io.BytesIO(data)) as picture:
                    require(picture.format == {'png': 'PNG', 'jpg': 'JPEG'}[kind], '文件内容与选择的图片类型不符。')
                    require(picture.width * picture.height <= 24000000 and max(picture.size) <= 16384, '图片分辨率过大。')
                    require(getattr(picture, 'n_frames', 1) == 1, '请上传静态卡面。')
                    picture.verify()
                with Image.open(io.BytesIO(data)) as picture:
                    picture.load()
    except SubmissionError:
        raise
    except Exception:
        raise SubmissionError('附件无法解码，请上传有效的图片或单页 PDF。') from None


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=4) + '\n').encode('utf-8')


def validate_catalog(data):
    require(isinstance(data, dict) and set(data) == {'cards'} and isinstance(data['cards'], list), 'cards.json 结构无效，停止写入以保留现有内容。')
    require(len(data['cards']) <= 10000, '卡面索引过大。')
    for card in data['cards']:
        require(isinstance(card, dict) and set(card) == FIELDS, '已有卡面字段不符合索引结构。')
        require(all(isinstance(card[k], str) for k in FIELDS - {'fanmade'}) and type(card['fanmade']) is bool, '已有卡面字段类型无效。')
    return data


class GitHub:
    def __init__(self, token):
        require(bool(token), '缺少 GitHub Token。')
        self.token = token
        self.opener = urllib.request.build_opener(NoRedirect())

    def api(self, path, method='GET', body=None):
        require(path.startswith('/') and not path.startswith('//'), '无效的 API 路径。')
        payload = json_bytes(body) if body is not None else None
        request = urllib.request.Request('https://api.github.com/repos/' + REPOSITORY + path, data=payload, method=method, headers={
            'Authorization': 'Bearer ' + self.token, 'User-Agent': 'AirCard-Submissions',
            'Accept': 'application/vnd.github+json', 'Content-Type': 'application/json',
            'X-GitHub-Api-Version': '2022-11-28',
        })
        try:
            with self.opener.open(request, timeout=30) as response:
                raw = response.read(4 * MAX_JSON + 1)
                require(len(raw) <= 4 * MAX_JSON, 'GitHub API 响应过大。')
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as error:
            error.close()
            raise ApiError(error.code) from None

    def read_json(self, path, commit):
        try:
            result = self.api('/contents/' + path + '?ref=' + commit)
        except ApiError as error:
            if error.status == 404:
                return None
            raise
        require(result.get('type') == 'file' and result.get('encoding') == 'base64' and result.get('size', MAX_JSON + 1) <= MAX_JSON, '仓库索引不是可读取的 JSON 文件。')
        data = base64.b64decode(result['content'].replace('\n', ''), validate=True)
        require(len(data) == result['size'], '仓库 JSON 文件读取不完整。')
        return json.loads(data)

    def blob(self, data):
        return self.api('/git/blobs', 'POST', {'content': base64.b64encode(data).decode('ascii'), 'encoding': 'base64'})['sha']


def verify_approval(api, event, actor=None):
    require(event.get('action') == 'labeled' and event.get('label', {}).get('name') == APPROVAL, '只处理审核通过标签事件。')
    require(event.get('repository', {}).get('full_name') == REPOSITORY, '仓库不匹配。')
    issue = event.get('issue', {})
    number = issue.get('number')
    require(type(number) is int and number > 0 and 'pull_request' not in issue, '只处理卡面投稿 Issue。')
    for reviewer in {event.get('sender', {}).get('login'), actor} - {None, ''}:
        require(re.fullmatch(r'[a-zA-Z0-9-]+(?:\[bot\])?', reviewer), '审核者标识无效。')
        permission = api.api('/collaborators/' + urllib.parse.quote(reviewer, safe='') + '/permission')
        require(permission.get('permission') in ('admin', 'maintain', 'write'), '只有仓库维护者可以批准投稿。')
    require(event.get('sender', {}).get('login'), '缺少审核者。')
    current = api.api('/issues/' + str(number))
    labels = {label['name'] for label in current.get('labels', [])}
    require(current.get('state') == 'open' and {APPROVAL, SUBMISSION} <= labels, '投稿已关闭或缺少审核标签。')
    require(current.get('body') == issue.get('body'), '投稿在审核后发生修改，请移除审核通过标签，重新检查后再添加。')
    return number


def publish(api, event, card, data, actor=None):
    number = verify_approval(api, event, actor)
    validate_file(data, card['type'])
    checksum = hashlib.sha256(data).hexdigest()
    path = 'files/' + checksum + '.' + card['type']
    source = 'https://raw.githubusercontent.com/' + REPOSITORY + '/' + BRANCH + '/' + path
    record = dict(card, source=source)
    body_digest = hashlib.sha256(event['issue']['body'].encode('utf-8')).hexdigest()
    receipt_path = '.submissions/' + str(number) + '.json'
    asset_blob = None
    for attempt in range(3):
        parent = api.api('/git/ref/heads/' + BRANCH)['object']['sha']
        receipt = api.read_json(receipt_path, parent)
        if receipt is not None:
            require(receipt.get('body_sha256') == body_digest and receipt.get('file_sha256') == checksum, '该投稿已收录；更新卡面请新建投稿。')
            return receipt['source'], False
        existing = api.read_json('cards.json', parent)
        catalog = validate_catalog({'cards': []} if existing is None else existing)
        catalog['cards'].append(record)
        contents = json_bytes(catalog)
        require(len(contents) <= 1024 * 1024, 'cards.json 已达到 1 MiB，请维护者调整索引存储后重试。')
        base_tree = api.api('/git/commits/' + parent)['tree']['sha']
        if asset_blob is None:
            asset_blob = api.blob(data)
        receipt = {'issue': number, 'body_sha256': body_digest, 'file_sha256': checksum, 'source': source}
        tree = api.api('/git/trees', 'POST', {'base_tree': base_tree, 'tree': [
            {'path': path, 'mode': '100644', 'type': 'blob', 'sha': asset_blob},
            {'path': 'cards.json', 'mode': '100644', 'type': 'blob', 'sha': api.blob(contents)},
            {'path': receipt_path, 'mode': '100644', 'type': 'blob', 'sha': api.blob(json_bytes(receipt))},
        ]})['sha']
        commit = api.api('/git/commits', 'POST', {
            'message': 'Add approved card from issue #' + str(number), 'tree': tree, 'parents': [parent],
        })['sha']
        verify_approval(api, event, actor)
        try:
            api.api('/git/refs/heads/' + BRANCH, 'PATCH', {'sha': commit, 'force': False})
            return source, True
        except ApiError as error:
            if error.status not in (409, 422) or api.api('/git/ref/heads/' + BRANCH)['object']['sha'] == parent:
                raise
    raise SubmissionError('cards 分支正在被更新，请重新运行本次工作流。')


def main():
    require(os.environ.get('GITHUB_REPOSITORY') == REPOSITORY, '仅在目标仓库运行。')
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
    api = GitHub(os.environ.get('GH_TOKEN'))
    actor = os.environ.get('GITHUB_TRIGGERING_ACTOR')
    verify_approval(api, event, actor)
    card, url = parse_submission(event['issue']['body'])
    source, created = publish(api, event, card, download_attachment(url), actor)
    summary = ('已收录卡面。' if created else '该投稿已收录，无需重复写入。') + '\n\n下载地址：' + source + '\n'
    print(summary)
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as output:
            output.write(summary)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Avoid printing user text, signed attachment URLs or request headers.
        message = str(error) if isinstance(error, SubmissionError) else '投稿处理失败，请检查附件及工作流配置。'
        print(message, file=sys.stderr)
        sys.exit(1)
