# Repository guidance
Use .NET 10, file-scoped namespaces and readable C#.
Build with dotnet build and test with dotnet test -c Release.
Exercise the packed NuGet consumer before a release.
Keep eligibility, feasible-set coverage and optimisation guarantees explicit.
Publish through GitHub OIDC in release.yml under the nuget environment.
