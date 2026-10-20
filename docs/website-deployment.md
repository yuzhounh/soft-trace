# 产品网站发布

产品网站源码为 `site/`，包含产品主页、Google 登录与个人云端仪表盘；采集仍由原程序或扩展负责。只发布必要展示资源，不上传本地数据库或登录凭据。

- 网站名称：`soft-trace`。
- GitHub 仓库：`yuzhounh/soft-trace`；Pages 入口使用现有仓库路径。
- 构建：`node scripts/build-site.mjs`；输出 `site/dist/`，含公开文件 SHA-256 和源码提交清单 `release.json`。
- GitHub Pages：工作流 `.github/workflows/pages.yml`，只上传静态发布目录。
- Cloudflare Pages、Vercel：发布同一静态目录；先确认账号、项目及原名地址归属。
- Firebase：`firebase deploy --config site/firebase.json --only hosting --project soft-trace`；仅操作 Hosting 站点 `soft-trace`，不修改数据库规则、认证或其他站点。
- Netlify：只注册独立站点且不连接 Git 自动构建。2026-10-21 15:00 Asia/Shanghai 前不部署；之后遵守每项目滚动 168 小时最多一次提交，记录以维护 Hub 的 `review/netlify_deployments.md` 为准。
- 普通 GitHub 推送提交保留 `[skip netlify]`。

现有应用功能改动未纳入本次网站提交；网站下载链接指向现有公开发行版。

## 当前发布状态（2026-10-20）

网站源码提交：`d7b55f4ab605a5af60f0528c55699913ea9daa32`。

- GitHub Pages：https://yuzhounh.github.io/soft-trace/；工作流 `39376890939` 成功。
- Cloudflare Pages：https://soft-trace.pages.dev/；部署 `479316f3`。
- Vercel：https://soft-trace.vercel.app/；部署 `dpl_BVVt2Yak9QN5pJxSoqRxeqe5ovDn` Ready。
- Firebase Hosting：https://soft-trace.web.app/；与个人主页同属 Firebase 工程，但仅发布 `soft-trace` 站点。
- Netlify：https://soft-trace.netlify.app/；站点 `dc73b9a9-126a-44eb-bbbb-155329403219` 已注册，未连接 Git、未部署。用户仅批准注册例外，部署仍暂停至 2026-10-21 15:00。

四个正式首页、隐私页、CSS、图标和发布清单均核验通过，共 76 项公开文件与本地构建一致（Soft Trace 每入口 19 项、Time Lens 每入口 21 项；含发布清单）。平台回执及验证结果保存于 `results/website/`。GitHub Pages 沿用现有仓库路径，没有重命名仓库或创建第二个源码仓库。

## Google 登录与云端仪表盘

网站首页提供 Google 登录，登录后进入 app/。界面取自当前本地页面的展示层，网站专用适配器仅用当前登录用户的 Firebase ID Token 读取本人记录；不连接 localhost，不执行采集、导入、删除或设备改名。账号切换后重置内存缓存；退出后返回首页。浏览器保留 Firebase 登录会话，不把浏览记录写入网站发布包。

Soft Trace 按真实活动起止时间查询并分页，兼容旧状态记录的 stateStartUtc/stateEndUtc，时间轴以区间并集计算实际使用。Time Lens 按设备清单读取按日分块记录，校验 SHA-256，保留小时统计和原有图表逻辑。静态包包含页面、展示脚本和公开 Firebase 客户端配置，不包含用户数据或管理凭据。

网页仪表盘以云端已成功同步的记录为准；本机新记录需等待原设备同步。网站安全策略允许界面所需的内联样式，脚本来源仍限定为本站与 Google 官方 SDK；Firebase 托管认证辅助地址不应用页面框架限制。

本轮将账户信息、刷新和退出统一收进设置，移除额外顶栏；主页按钮“打开网页面板”位于两个下载/安装按钮右侧。Time Lens 标签标题为“Time Lens · 时光镜”。Soft Trace 逐段明细的滚动条使用更浅颜色；同一颜色修改已写回本地网页源码。

Google 登录由用户在真实浏览器完成；Soft Trace 已展示云端活动记录。Time Lens 分块校验已对齐扩展现有 SHA-256 前 32 位格式，用户刷新后确认图表和网站排行正常。自动化浏览器连接后续暂时失效，因此本轮不将退出操作标记为真实浏览器验收通过；账号退出逻辑调用 Firebase signOut，原有云端数据不会被删除。静态资源与账户隔离、跨午夜统计/分块完整性检查通过。
