$ErrorActionPreference = 'Stop'
function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $Arguments failed with exit code $LASTEXITCODE" }
}
Invoke-Dotnet @('build', 'ShadowServerCommand.sln', '-c', 'Release')
foreach ($suite in @('Unit', 'Integration', 'Security')) {
    Invoke-Dotnet @('test', "tests/SSC.${suite}Tests", '-c', 'Release', '--no-build', '--logger', "trx;LogFileName=$suite.trx", '--results-directory', 'TestResults')
}
