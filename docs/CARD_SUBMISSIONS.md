# 投稿卡面

点击 Air Card 的“投稿卡面…”按钮，或打开[投稿表单](https://github.com/Feryquence/AirCard/issues/new?template=card-submission.yml)。使用 GitHub 账号登录，无需 Fork，也无需提交 PR。

填写名称、上传者、描述（可留空）、是否二创以及文件类型，拖入一个附件并等待上传完成，再提交。PNG/JPG 不超过 10 MiB，PDF 不超过 25 MiB，PDF 须为未加密单页。上传者是投稿人填写的展示名称，不代表系统验证的作者身份。

## 审核与入库

维护者确认文字和附件后，在该 Issue 上添加 **审核通过** 标签。保留自动添加的 **卡面投稿** 标签。GitHub Actions 会校验审核者权限、投稿内容、附件格式和大小，将文件及 JSON 记录一起提交到 `cards` 分支；成功后的下载链接显示在该次 Actions 运行摘要中。

若审核后修改了投稿内容，旧工作流会拒绝入库。请移除“审核通过”标签，重新检查后再添加。网络故障可在 Actions 页面重新运行失败任务。同一条投稿只收录一次；已收录卡面需要更新时请新建投稿。

投稿流程不会向 `main` 合并 `cards`，也不会合并投稿人的代码。表单和工作流配置保存在 `main`；公开卡面文件保存在 `cards`。**无需将两个分支互相合并。**

## 索引格式

`cards` 分支根目录的 `cards.json` 使用如下结构：

```json
{
    "cards": [
        {
            "name": "suica",
            "uploader": "Feryquence",
            "description": "",
            "fanmade": false,
            "type": "pdf",
            "source": "https://raw.githubusercontent.com/Feryquence/AirCard/cards/files/<SHA256>.pdf"
        }
    ]
}
```

`source` 由系统生成，不需要投稿人填写。文件按内容的 SHA-256 命名，避免名称重复覆盖。文件内容原样保存，不裁切、不转码。`type` 为 `png`、`jpg` 或 `pdf`，`fanmade` 是 JSON 布尔值。描述未填写时为 `""`。

索引地址：[cards.json](https://raw.githubusercontent.com/Feryquence/AirCard/cards/cards.json)。`.submissions/<Issue编号>.json` 保存入库回执，用于重复运行去重，不属于卡面列表。

不同投稿并行写入时采用非强制更新和重试合并，不覆盖他人的新提交。工作流只下载 GitHub 投稿附件，重定向限制到 GitHub 附件服务；附件请求不携带仓库令牌。附件校验不等于版权或作者身份审核，是否收录由维护者决定。

## 维护与检查

工作流名称为“卡面投稿入库”。手动运行该工作流仅执行离线测试，不发布卡面；只有受信任维护者添加“审核通过”标签才会触发入库。无需配置个人 Token，使用权限限于当前仓库的 `GITHUB_TOKEN`。保留仓库 Issues 和 Actions 功能开启。

本地检查：

```text
python -m pip install -r .github/scripts/requirements.txt
python -m unittest discover -s Tests -p test_card_submissions.py -v
```
