<#
.SYNOPSIS
只运行与改动有关的测试项目。

.DESCRIPTION
改动 = 自 -Base 与 HEAD 的分叉点以来的提交，加上工作区里未提交与未跟踪的文件。
- 文件属于某个项目时，沿 ProjectReference 选出直接或间接引用该项目的测试项目（含它自己）；
  改了 Core 或共享测试库 Junevy.Communication.Testing，按依赖关系自然会选中几乎全部测试项目。
- 不属于任何项目、也不进入任何程序集的文件不触发测试：文档（*.md、docs/、知识库、Skills/）、build/ 下的脚本，
  以及解决方案文件（它只列出项目；新增的项目由它自己的目录归属，解决方案能否构建由 dotnet build 把关）。
- 不属于任何项目的其他文件（build/ 下的 props、app.config 与 Polyfills 等）是共享构建输入，运行全部测试。

.PARAMETER Base
比较基准（分支名或提交）。默认 master。

.PARAMETER Repeat
连续运行的遍数，默认 1；验收复跑传 3。

.PARAMETER All
不看改动，运行整个解决方案的测试。

.PARAMETER ListOnly
只列出改动的项目与选中的测试项目，不运行。

.PARAMETER NoBuild
传给 dotnet test 的 --no-build（已经构建过解决方案时使用）。

.EXAMPLE
pwsh build/test-affected.ps1 -Base 40c3a63 -Repeat 3 -NoBuild
#>
[CmdletBinding()]
param(
    [string]$Base = 'master',
    [ValidateRange(1, 20)][int]$Repeat = 1,
    [switch]$All,
    [switch]$ListOnly,
    [switch]$NoBuild,
    [string]$Configuration = 'Debug'
)

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'Junevy.Communication.slnx'
$separator = [IO.Path]::DirectorySeparatorChar

# 原生命令的输出按 [Console]::OutputEncoding 解码，而它取自当前控制台代码页（中文系统上是 cp936）。
# dotnet 输出 UTF-8，不改的话下面匹配 "已通过!" 的那几行全是乱码，每遍合计会安静地报成 0。
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 不属于任何项目、也不进入任何程序集的文件。
$ignoredPatterns = @('*.md', 'docs/*', 'Junevy.Communication.Wiki/*', 'Skills/*', 'build/metrics/*', 'build/*.ps1', '*.slnx', '.gitignore', '.zcodeignore', '.superpowers/*')

function Resolve-FullPath([string]$directory, [string]$relative) {
    [IO.Path]::GetFullPath((Join-Path $directory ($relative -replace '[\\/]', $separator)))
}

function Get-Projects {
    $solutionXml = [xml](Get-Content $solution -Raw)
    foreach ($node in $solutionXml.SelectNodes('//Project')) {
        $path = Resolve-FullPath $root $node.Path
        $directory = Split-Path $path
        $projectXml = [xml](Get-Content $path -Raw)
        [pscustomobject]@{
            Path       = $path
            Directory  = $directory + $separator
            Name       = [IO.Path]::GetFileNameWithoutExtension($path)
            IsTest     = $path -like "*$($separator)Tests$($separator)*.Tests$($separator)*"
            # 带条件的引用（例如只在 net8.0 生效的）也算，宁可多跑。
            References = @($projectXml.SelectNodes('//ProjectReference') | ForEach-Object { Resolve-FullPath $directory $_.Include })
        }
    }
}

function Get-ChangedFiles {
    $mergeBase = git -C $root merge-base $Base HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Cannot find the merge base of '$Base' and HEAD." }

    $previous = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        # 与工作区比较，因此已暂存、未暂存的改动都算在内；未跟踪的文件另外列出。
        @(git -C $root -c core.quotePath=false diff --name-only $mergeBase 2>$null) +
            @(git -C $root -c core.quotePath=false ls-files --others --exclude-standard 2>$null)
    }
    finally {
        [Console]::OutputEncoding = $previous
    }
}

function Select-TestTargets {
    $projects = @(Get-Projects)
    $dependents = @{}
    foreach ($project in $projects) {
        foreach ($reference in $project.References) {
            if (-not $dependents.ContainsKey($reference)) { $dependents[$reference] = @() }
            $dependents[$reference] += $project
        }
    }

    $changed = @{}
    $shared = @()
    foreach ($file in (Get-ChangedFiles | Sort-Object -Unique)) {
        $fullPath = Resolve-FullPath $root $file
        $owner = $projects | Where-Object { $fullPath.StartsWith($_.Directory, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if ($owner) { $changed[$owner.Path] = $owner }
        elseif (-not ($ignoredPatterns | Where-Object { $file -like $_ })) { $shared += $file }
    }

    if ($shared) {
        Write-Host "Shared build inputs changed, running every test project: $($shared -join ', ')"
        return @($solution)
    }

    Write-Host "Changed projects: $(if ($changed.Count) { ($changed.Values.Name | Sort-Object) -join ', ' } else { '(none)' })"
    $selected = @{}
    $queue = [Collections.Generic.Queue[object]]::new([object[]]@($changed.Values))
    while ($queue.Count) {
        $project = $queue.Dequeue()
        if ($selected.ContainsKey($project.Path)) { continue }
        $selected[$project.Path] = $project
        foreach ($dependent in @($dependents[$project.Path])) { if ($dependent) { $queue.Enqueue($dependent) } }
    }

    @($selected.Values | Where-Object IsTest | Sort-Object Name | ForEach-Object Path)
}

function Measure-Run([string[]]$summaryLines) {
    $totals = @{ Passed = 0; Failed = 0; Skipped = 0 }
    foreach ($line in $summaryLines) {
        if ($line -match '(?:通过|Passed):\s+(\d+)') { $totals.Passed += [int]$Matches[1] }
        if ($line -match '(?:失败|Failed):\s+(\d+)') { $totals.Failed += [int]$Matches[1] }
        if ($line -match '(?:已跳过|Skipped):\s+(\d+)') { $totals.Skipped += [int]$Matches[1] }
    }
    "passed $($totals.Passed), failed $($totals.Failed), skipped $($totals.Skipped), $($summaryLines.Count) test hosts"
}

$targets = if ($All) { @($solution) } else { @(Select-TestTargets) }
if (-not $targets) {
    Write-Host 'No test project is affected by the changes.'
    exit 0
}

Write-Host "Test targets:`n  $(($targets | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }) -join "`n  ")"
if ($ListOnly) { exit 0 }

$anyFailure = $false
for ($run = 1; $run -le $Repeat; $run++) {
    Write-Host "`n=== Run $run of $Repeat"
    $summaryLines = @()
    foreach ($target in $targets) {
        $testArguments = @('test', $target, '-c', $Configuration, '--nologo')
        if ($NoBuild) { $testArguments += '--no-build' }
        $output = & dotnet @testArguments 2>&1 | ForEach-Object { "$_" }
        $exitCode = $LASTEXITCODE
        $lines = @($output | Where-Object { $_ -match '(?:Passed|Failed|已通过|失败)!' } | ForEach-Object { $_.Trim() })
        $lines | ForEach-Object { Write-Host "  $_" }
        $summaryLines += $lines
        if ($exitCode -ne 0) {
            $anyFailure = $true
            Write-Host "--- dotnet test $target exited with $exitCode; full output follows"
            $output | ForEach-Object { Write-Host $_ }
        }
    }
    Write-Host "Run $($run): $(Measure-Run $summaryLines)"
}

exit $(if ($anyFailure) { 1 } else { 0 })
