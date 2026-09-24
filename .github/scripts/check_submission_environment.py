"""Read-only live API check for the workflow's built-in GitHub token."""
import os
import urllib.parse
from card_submissions import GitHub, validate_catalog, require, BRANCH, REPOSITORY

require(os.environ.get('GITHUB_REPOSITORY') == REPOSITORY, '仓库不匹配。')
api = GitHub(os.environ.get('GH_TOKEN'))
parent = api.api('/git/ref/heads/' + BRANCH)['object']['sha']
validate_catalog(api.read_json('cards.json', parent))
actor = os.environ['GITHUB_ACTOR']
permission = api.api('/collaborators/' + urllib.parse.quote(actor, safe='') + '/permission')
require(permission.get('permission') in ('admin', 'maintain', 'write'), '工作流启动者需要维护权限。')
print('PASS: built-in token can read the card catalog and verify reviewer permissions. No writes performed.')
