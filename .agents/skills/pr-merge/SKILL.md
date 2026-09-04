---
name: pr-merge
description: "PR 合并收尾流程。仅在用户明确要求时触发：用户说'走一个PR'、'提个PR合并主干'、'合并到主干'、'pr-merge'时使用。绝不在用户未主动提出时自动触发——不要把它当作提交代码的默认方式。"
---

# PR Merge

把当前工作区的开发改动整理成 feature 分支，走 PR 合并回主干，然后让仓库回到干净的主干状态。这是本仓库固定的收尾流程，不要跳步、不要改成直接 push main。

## 第 0 步：确认触发意图

此技能**只能由用户显式请求**触发（例如"走一个 PR 合并主干吧"）。如果用户只是让你改代码或提交代码，不要加载此技能；此时应遵循 `AGENTS.md` 的常规验证与部署 workflow。

## 第 1 步：盘点改动

```bash
git status --short
git log --oneline -3
```

- 逐个查看未跟踪的目录/文件，**按内容判断**是否属于本次功能改动（而不是一律 `git add .`）。
- 会话产物（`.zcode/`）、临时调研笔记（`docs/todo/`）等不属于仓库的内容：确认已被 `.gitignore` 覆盖；未覆盖则追加 ignore 规则并单独提交。
- 与用户确认拿不准的文件（用 AskUserQuestion，按内容给出建议选项）。

## 第 2 步：分支与提交

```bash
git checkout -b feature/<kebab-case-feature-slug>
git add <逐项列出的路径>
git commit -m "<标题行 + 空行 + 要点 + Testing/Docs 说明>"
```

- 分支名用 `feature/` 前缀 + 简短英文 slug（参考本次功能主题）。
- 提交信息遵循 `AGENTS.md` 的 PR Message Requirements：功能要点、Testing 命令与结果、Docs 更新。
- 提交前确认构建/测试已通过（`AGENTS.md` Testing Expectations）；未跑就先跑。

## 第 3 步：推送并创建 PR

```bash
git push -u origin feature/<slug>
gh pr create --title "<功能标题>" --body "<Summary / Functional Changes / Testing Commands & Outcomes / Documentation Updates>"
```

- PR body 用中文书写，四大段结构固定（与上一次 PR 保持一致，见 `gh pr view` 历史样例）。

## 第 4 步：合并并清理

```bash
gh pr merge <PR号> --merge --delete-branch
git checkout main && git pull origin main
git branch -d feature/<slug>   # 若 --delete-branch 已删掉则跳过
```

- 合并方式用 merge commit（保留分支历史），不 squash。
- 删除本地与远端 feature 分支。

## 第 5 步：回归干净主干

- `git status` 必须干净（允许被 .gitignore 覆盖的本地产物存在）。
- 若有遗留 ignore 缺口（如步骤 1 追加的规则），在 main 上补一个小的 `.gitignore` 提交并直接 push（这类整理性提交不需要走 PR）。
- 最终向用户报告：PR 链接、合并 commit、当前 main 状态。

## 常见坑

- `gh pr create` 会警告 uncommitted changes——先完成第 1 步的判断再提交，警告只针对确实不入库的文件。
- 删分支前确认合并真的完成（`gh pr view <PR号> --json state`），避免误删未合并工作。
- 如果工作区同时存在多个不相关主题的改动，先问用户怎么拆分，不要自作主张混进一个 PR。