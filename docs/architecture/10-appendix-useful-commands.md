# 10. Appendix - Useful Commands

(源自 `README.md`)

```powershell
# Start interactive command line
dotnet run --project .\GameHelper.ConsoleHost --

# 启动监控 (默认 WMI)
dotnet run --project .\GameHelper.ConsoleHost -- monitor

# 使用 ETW 监控 (需要管理员权限)
dotnet run --project .\GameHelper.ConsoleHost -- monitor --monitor-type ETW

# 查看统计
dotnet run --project .\GameHelper.ConsoleHost -- stats

# 配置管理
dotnet run --project .\GameHelper.ConsoleHost -- config list
```
