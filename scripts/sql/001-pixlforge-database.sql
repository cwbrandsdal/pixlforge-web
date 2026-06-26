/*
    PixlForge SQL Server setup.

    Run against master as a SQL Server administrator, then create a least-privileged
    app login/user and store its connection string outside the repo.
*/

if db_id(N'PixlForge') is null
begin
    create database PixlForge;
end;
go

use PixlForge;
go

if object_id(N'dbo.PixlForgeUserState', N'U') is null
begin
    create table dbo.PixlForgeUserState
    (
        UserKey nvarchar(256) not null constraint PK_PixlForgeUserState primary key,
        StateJson nvarchar(max) not null,
        UpdatedAt datetimeoffset not null constraint DF_PixlForgeUserState_UpdatedAt default sysdatetimeoffset()
    );
end;
go

if object_id(N'dbo.PixlForgeSecrets', N'U') is null
begin
    create table dbo.PixlForgeSecrets
    (
        UserKey nvarchar(256) not null,
        SecretName nvarchar(128) not null,
        SecretValue nvarchar(max) not null,
        UpdatedAt datetimeoffset not null constraint DF_PixlForgeSecrets_UpdatedAt default sysdatetimeoffset(),
        constraint PK_PixlForgeSecrets primary key (UserKey, SecretName)
    );
end;
go

/*
    Optional least-privileged app user setup.

    Run with sqlcmd variables, for example:
    sqlcmd -S 192.168.1.128,1433 -U sa -P "<admin password>" -i 001-pixlforge-database.sql -v PixlForgeAppPassword="<strong password>"
*/

if '$(PixlForgeAppPassword)' <> '' and '$(PixlForgeAppPassword)' not like '$(%'
begin
    declare @loginSql nvarchar(max);

    if exists (select 1 from sys.sql_logins where name = N'pixlforge_app')
        set @loginSql = N'alter login pixlforge_app with password = ' + quotename('$(PixlForgeAppPassword)', '''') + N';';
    else
        set @loginSql = N'create login pixlforge_app with password = '
            + quotename('$(PixlForgeAppPassword)', '''') + N', check_policy = on;';

    exec sys.sp_executesql @loginSql;
end;
go

use PixlForge;
go

if exists (select 1 from sys.server_principals where name = N'pixlforge_app')
    and not exists (select 1 from sys.database_principals where name = N'pixlforge_app')
begin
    create user pixlforge_app for login pixlforge_app;
end;
go

if exists (select 1 from sys.database_principals where name = N'pixlforge_app')
begin
    grant select, insert, update, delete on dbo.PixlForgeUserState to pixlforge_app;
    grant select, insert, update, delete on dbo.PixlForgeSecrets to pixlforge_app;
end;
go
