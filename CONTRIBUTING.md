# Contributing

Use .NET 10 and keep dependencies directed toward `LLMProxy.Domain`. Provider-specific translation belongs in `LLMProxy.Providers`; routing and policy must not branch on provider names. Persistence implementations belong in `Infrastructure`; domain/application code must not depend on EF Core.

1. Create a branch and keep changes focused.
2. Add meaningful tests for protocol behavior, error paths or concurrency changes. All provider calls in automated tests must be mocked.
3. Run `dotnet restore --locked-mode`, `dotnet build -c Release`, `dotnet test -c Release` and `dotnet format --verify-no-changes`.
4. For wire-format, deployment or streaming changes, run the SDK and Docker smoke tests described in the README.
5. Update API/configuration documentation when behavior changes and include migration steps for schema changes.

Package versions are managed centrally in `Directory.Packages.props`. After an intentional dependency update, run `dotnet restore` and commit affected lock files. Do not update lock files merely to bypass an unexpected restore failure.

Generate migrations for **both** storage engines:

```bash
dotnet tool restore
dotnet ef migrations add ChangeName --project src/LLMProxy.Infrastructure \
  --context PostgresGatewayDbContext --output-dir Persistence/Migrations/Postgres
dotnet ef migrations add ChangeName --project src/LLMProxy.Infrastructure \
  --context SqliteGatewayDbContext --output-dir Persistence/Migrations/Sqlite
```

The design-time factories use local placeholder connection settings and do not embed credentials. Review generated migrations before committing. Integration tests create isolated databases and run the actual migrations.

Never commit provider keys, gateway keys, `.env`, certificate files, real prompts or live request/response captures. Avoid logging exception objects from provider/network code; they can contain sensitive details. Preserve cancellation through every async boundary, and never retry a stream once content has been emitted.

The Docker image is the product. Optional reusable packages must remain independent of the standalone deployment and must not make normal users build or install a NuGet package.
