# 产品网站发布

产品网站源码为 `site/`，仅介绍产品和提供安装、下载及隐私入口。桌面程序、Chrome 扩展及其本地数据不作为静态站点上传。

- 网站名称：`soft-trace`。
- GitHub 仓库：`yuzhounh/soft-trace`；Pages 入口使用现有仓库路径。
- 构建：`node scripts/build-site.mjs`；输出 `site/dist/`，含公开文件 SHA-256 和源码提交清单 `release.json`。
- GitHub Pages：工作流 `.github/workflows/pages.yml`，只上传静态发布目录。
- Cloudflare Pages、Vercel：发布同一静态目录；先确认账号、项目及原名地址归属。
- Firebase：`firebase deploy --config site/firebase.json --only hosting --project soft-trace`；仅操作 Hosting 站点 `soft-trace`，不修改数据库规则、认证或其他站点。
- Netlify：只注册独立站点且不连接 Git 自动构建。2026-10-21 15:00 Asia/Shanghai 前不部署；之后遵守每项目滚动 168 小时最多一次提交，记录以维护 Hub 的 `review/netlify_deployments.md` 为准。
- 普通 GitHub 推送提交保留 `[skip netlify]`。

现有应用功能改动未纳入本次网站提交；网站下载链接指向现有公开发行版。
