import { registerMessages } from './locale';

export const pageMeta: Record<string, { eyebrow: string; description: string }> = {
  dashboard: { eyebrow: '今日概览', description: '查看当前运行状态、调度概览和通知能力。' },
  dispatch: { eyebrow: '调度中心', description: '手动启动任务，观察实时输出并及时取消运行。' },
  queues: { eyebrow: '调度队列', description: '把多个脚本串成一个可重复执行的工作流。' },
  scripts: { eyebrow: '脚本实例', description: '管理脚本入口、用户配置和运行策略。' },
  users: { eyebrow: '用户管理', description: '统一管理用户头像、脚本绑定、运行优先级和通知设置。' },
  history: { eyebrow: '运行历史', description: '按日期与账号查阅运行结果、完整日志和截图。' },
  plugins: { eyebrow: '插件', description: '查看已安装插件并管理运行状态。' },
  notifications: {
    eyebrow: '通知管理',
    description: '管理可复用的通知任务，并在用户、队列和签到任务中选择发送目标。'
  },
  settings: { eyebrow: '设置', description: '集中管理服务行为、通知渠道、远程访问、代理与更新设置。' }
};
registerMessages({
  'SMTP 密码': 'SMTP password',
  'Webhook 地址': 'Webhook URL',
  'Webhook 密钥': 'Webhook secret',
  工作空间: 'Workspace',
  管理: 'Management',
  扩展与系统: 'Extensions & system',
  调度中心: 'Scheduler',
  调度队列: 'Schedule queues',
  脚本实例: 'Script instance',
  用户管理: 'User management',
  通知管理: 'Notification management',
  '查看当前运行状态、调度概览和通知能力。':
    'View current run status, scheduling overview, and notification capabilities.',
  '手动启动任务，观察实时输出并及时取消运行。':
    'Start tasks manually, watch live output, and cancel runs when needed.',
  '把多个脚本串成一个可重复执行的工作流。': 'Turn multiple scripts into a repeatable workflow.',
  '管理脚本入口、用户配置和运行策略。': 'Manage script entry points, user configuration, and run policies.',
  '统一管理用户头像、脚本绑定、运行优先级和通知设置。':
    'Manage user avatars, script bindings, run priority, and notification settings.',
  '按日期与账号查阅运行结果、完整日志和截图。':
    'Browse results, full logs and screenshots by date and account.',
  '查看已安装插件并管理运行状态。': 'View installed plugins and manage runtime status.',
  '管理可复用的通知任务，并在用户、队列和签到任务中选择发送目标。':
    'Manage reusable notification tasks for users, queues and check-in tasks.',
  '集中管理服务行为、通知渠道、远程访问、代理与更新设置。':
    'Manage service behavior, notifications, remote access, proxy, and update settings in one place.',
  通用脚本: 'Generic script',
  新建脚本实例: 'New script instance',
  添加用户: 'Add user',
  新建调度队列: 'New schedule queue',
  添加通知任务: 'Add notification task',
  本地插件: 'Local plugins',
  插件仓库: 'Plugin repository',
  编辑页面布局: 'Edit page layout',
  一切准备就绪: 'Everything is ready',
  '通过邮件服务器发送通知。': 'Send notifications through an email server.',
  目标类型: 'Target type',
  '手动配置主程序、启动参数、配置与日志路径':
    'Configure an executable, arguments, configuration and log paths.',
  '发送到飞书、企业微信、Slack、Discord 或自定义 Webhook。':
    'Send to Feishu, WeCom, Slack, Discord or a custom webhook.',
  '服务启动、历史记录与日志选项': 'Startup, history retention and logging',
  '远程管理入口与本机 Agent 服务': 'Remote access and the local agent service',
  '宿主外部 HTTP/HTTPS 请求': 'Outbound HTTP and HTTPS connections',
  控制需要手动确认的配置修复行为: 'Configuration repair and advanced behavior',
  '更新渠道、检查与应用操作': 'Update channels, checks and installation',
  '检查运行环境、恢复现场与插件状态': 'Inspect the runtime, recovery state and extensions'
});
