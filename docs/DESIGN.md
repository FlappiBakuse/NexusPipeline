# 架构与当前行为

[文档门户](README.md)

## 1. 设计理念

[1. 设计理念](architecture/overview.md#1-设计理念)

## 2. 核心概念

[2. 核心概念](architecture/overview.md#2-核心概念)

## 3. 核心运行流程

[3. 核心运行流程](architecture/execution.md#3-核心运行流程)

### 3.1 脚本运行完整链路

[3.1 脚本运行完整链路](architecture/execution.md#31-脚本运行完整链路)

### 3.2 队列执行链路

[3.2 队列执行链路](architecture/scheduling.md#32-队列执行链路)

### 3.3 手动执行脚本

[3.3 手动执行脚本](architecture/execution.md#33-手动执行脚本)

### 3.4 统一控制面与 CLI

[3.4 统一控制面与 CLI](architecture/control.md#34-统一控制面与-cli)

### 3.5 MCP Agent 控制面

[3.5 MCP Agent 控制面](architecture/control.md#35-mcp-agent-控制面)

### 3.6 定期检查与闲时自动更新

[3.6 定期检查与闲时自动更新](architecture/update.md#36-定期检查与闲时自动更新)

## 4. 配置交换机制

[4. 配置交换机制](architecture/configuration.md#4-配置交换机制)

### 4.1 数据目录

[4.1 数据目录](architecture/configuration.md#41-数据目录)

### 4.2 运行前（PrepareForRun）与运行后（RestoreAfterRun）

[4.2 运行前（PrepareForRun）与运行后（RestoreAfterRun）](architecture/configuration.md#42-运行前prepareforrun与运行后restoreafterrun)

### 4.3 插队替换配置（replaceConfigs）

[4.3 插队替换配置（replaceConfigs）](architecture/configuration.md#43-插队替换配置replaceconfigs)

### 4.4 崩溃恢复（自愈）

[4.4 崩溃恢复（自愈）](architecture/recovery.md#44-崩溃恢复自愈)

### 4.5 自动更新配置：config → store 反向同步

[4.5 自动更新配置：config → store 反向同步](architecture/configuration.md#45-自动更新配置config-store-反向同步)

## 5. 完成判定机制

[5. 完成判定机制](architecture/judgement-logs.md#5-完成判定机制)

### 5.1 判定优先级

[5.1 判定优先级](architecture/judgement-logs.md#51-判定优先级)

### 5.2 判断脚本输入与触发

[5.2 判断脚本输入与触发](architecture/judgement-logs.md#52-判断脚本输入与触发)

### 5.3 判断脚本信任边界

[5.3 判断脚本信任边界](architecture/judgement-logs.md#53-判断脚本信任边界)

### 5.4 关键字模式

[5.4 关键字模式](architecture/judgement-logs.md#54-关键字模式)

## 6. 日志监控机制

[6. 日志监控机制](architecture/judgement-logs.md#6-日志监控机制)

### 6.1 日志路径解析（LogPattern.ResolveFile）

[6.1 日志路径解析（LogPattern.ResolveFile）](architecture/judgement-logs.md#61-日志路径解析logpatternresolvefile)

### 6.2 增量读取与三种文件形态

[6.2 增量读取与三种文件形态](architecture/judgement-logs.md#62-增量读取与三种文件形态)

### 6.3 超时语义

[6.3 超时语义](architecture/judgement-logs.md#63-超时语义)

## 7. 通知与数据落盘

[7. 通知与数据落盘](architecture/persistence-history.md#7-通知与数据落盘)

### 7.1 通知分发

[7.1 通知分发](architecture/observability.md#71-通知分发)

### 7.2 历史与日志落盘

[7.2 历史与日志落盘](architecture/persistence-history.md#72-历史与日志落盘)

### 7.3 插件仓库与安装事务

[7.3 插件仓库与安装事务](architecture/plugins.md#73-插件仓库与安装事务)

### 7.4 宿主代理设置与网络边界

[7.4 宿主代理设置与网络边界](architecture/observability.md#74-宿主代理设置与网络边界)

### 7.5 运行状态目录

[7.5 运行状态目录](architecture/persistence-history.md#75-运行状态目录)

### 7.6 文件布局治理规范

[7.6 文件布局治理规范](architecture/persistence-history.md#76-文件布局治理规范)

## 8. 已知行为与边界

[8. 已知行为与边界](architecture/overview.md#8-已知行为与边界)

### 8.1 已接受的设计约束

[8.1 已接受的设计约束](architecture/overview.md#81-已接受的设计约束)

## 10. 架构与模块定位（开发者导航）

[10. 架构与模块定位（开发者导航）](architecture/overview.md#10-架构与模块定位开发者导航)

### 10.1 总体结构

[10.1 总体结构](architecture/overview.md#101-总体结构)

### 10.2 后端分层与依赖方向（只允许向下依赖）

[10.2 后端分层与依赖方向（只允许向下依赖）](architecture/overview.md#102-后端分层与依赖方向只允许向下依赖)

### 10.3 关键类职责

[10.3 关键类职责](architecture/overview.md#103-关键类职责)

### 10.4 public / internal 约定

[10.4 public / internal 约定](architecture/overview.md#104-public-internal-约定)

### 10.5 新增 API 的落点

[10.5 新增 API 的落点](architecture/overview.md#105-新增-api-的落点)

### 10.6 控制面边界

[10.6 控制面边界](architecture/control.md#106-控制面边界)

### 10.7 前端分层

[10.7 前端分层](architecture/frontend.md#107-前端分层)

### 10.8 插件扩展指南

[10.8 插件扩展指南](architecture/plugins.md#108-插件扩展指南)

### 10.9 功能定位指南（找代码）

[10.9 功能定位指南（找代码）](architecture/overview.md#109-功能定位指南找代码)

### 10.10 数据流速览

[10.10 数据流速览](architecture/overview.md#1010-数据流速览)
