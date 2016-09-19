# Serilog.Sinks.SQLite
A lightweight high performance Serilog sink that writes to SQLite database.

## Getting started
Install [Serilog.Sinks.SQLite](https://www.nuget.org/packages/Serilog.Sinks.SQLite) from NuGet

```PowerShell
Install-Package Serilog.Sinks.SQLite
```

Configure logger by calling `WriteTo.SQLite()`

```C#
var logger = new LoggerConfiguration()
    .WriteTo.SQLite(@"Logs\log.db")
    .CreateLogger();
    
logger.Information("This informational message will be written to SQLite database");
```

## XML <appSettings> configuration

To use the rolling file sink with the [Serilog.Settings.AppSettings](https://www.nuget.org/packages/Serilog.Settings.AppSettings) package, first install that package if you haven't already done so:

```PowerShell
Install-Package Serilog.Settings.AppSettings
```
In your code, call `ReadFrom.AppSettings()`

```C#
var logger = new LoggerConfiguration()
    .ReadFrom.AppSettings()
    .CreateLogger();
```
In your application's App.config or Web.config file, specify the SQLite sink assembly and required **sqliteDbPath** under the `<appSettings>` node:

```XML
<appSettings>
    <add key="serilog:using:SQLite" value="Serilog.Sinks.SQLite"/>
    <add key="serilog:write-to:SQLite.sqliteDbPath" value="Logs\log.db"/>
    <add key="serilog:write-to:SQLite.tableName" value="Logs"/>
    <add key="serilog:write-to:SQLite.storeTimestampInUtc" value="true"/>
</appSettings>    
```

## Performance
Sink buffers log internally and flush to SQLite database in batches using dedicated thread.

[![Build status](https://ci.appveyor.com/api/projects/status/nc8ql37klw2njd86/branch/master?svg=true)](https://ci.appveyor.com/project/SaleemMirza/serilog-sinks-sqlite/branch/master)
