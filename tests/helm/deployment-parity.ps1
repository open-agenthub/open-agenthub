param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Read-RepoFile([string]$Path) {
    return Get-Content -Raw (Join-Path $repoRoot $Path)
}

function Assert-Matches {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Message
    )

    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-NotMatches {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Message
    )

    if ($Text -match $Pattern) {
        throw $Message
    }
}

$powerShellSetup = Read-RepoFile 'setup-dev.ps1'
$bashSetup = Read-RepoFile 'setup-dev.sh'

if (($powerShellSetup | Select-String -Pattern '(?m)^docker build ' -AllMatches).Matches.Count -ne 4) {
    throw 'setup-dev.ps1 must build exactly four images.'
}
if (($bashSetup | Select-String -Pattern '(?m)^docker build ' -AllMatches).Matches.Count -ne 4) {
    throw 'setup-dev.sh must build exactly four images.'
}

foreach ($setup in @($powerShellSetup, $bashSetup)) {
    Assert-Matches $setup 'open-agenthub-dev/backend:local' 'Local setup must build the backend image.'
    Assert-Matches $setup 'open-agenthub-dev/frontend:local' 'Local setup must build the frontend image.'
    Assert-Matches $setup 'open-agenthub-dev/agent-runtime-claude:local' 'Local setup must build the Claude runtime image.'
    Assert-Matches $setup 'open-agenthub-dev/agent-runtime-codex:local' 'Local setup must build the Codex runtime image.'
    Assert-NotMatches $setup 'open-agenthub-dev/agent-runtime:local' 'Local setup still builds the removed legacy runtime image.'
}

Assert-Matches $powerShellSetup "'agent-runtime/claude/Dockerfile'.*'agent-runtime'" 'PowerShell setup must use the Claude Dockerfile with the agent-runtime context.'
Assert-Matches $powerShellSetup "'agent-runtime/codex/Dockerfile'.*'agent-runtime'" 'PowerShell setup must use the Codex Dockerfile with the agent-runtime context.'
Assert-Matches $bashSetup 'agent-runtime/claude/Dockerfile" --tag .*agent-runtime-claude:local.*"\$script_dir/agent-runtime"' 'Bash setup must use the Claude Dockerfile with the agent-runtime context.'
Assert-Matches $bashSetup 'agent-runtime/codex/Dockerfile" --tag .*agent-runtime-codex:local.*"\$script_dir/agent-runtime"' 'Bash setup must use the Codex Dockerfile with the agent-runtime context.'

Assert-Matches $powerShellSetup '\$requiredContext = ''docker-desktop''' 'PowerShell setup lost the docker-desktop context requirement.'
Assert-Matches $bashSetup 'required_context=''docker-desktop''' 'Bash setup lost the docker-desktop context requirement.'
Assert-Matches $powerShellSetup 'Refusing to deploy: kubectl context' 'PowerShell setup lost the wrong-context refusal.'
Assert-Matches $bashSetup 'Refusing to deploy: kubectl context' 'Bash setup lost the wrong-context refusal.'

$devValues = Read-RepoFile 'helm/open-agenthub/values-dev.yaml'
Assert-Matches $devValues 'claude:\s*open-agenthub-dev/agent-runtime-claude:local' 'Development Helm values must select the locally built Claude runtime.'
Assert-Matches $devValues 'codex:\s*open-agenthub-dev/agent-runtime-codex:local' 'Development Helm values must select the locally built Codex runtime.'

$plainManifest = Read-RepoFile 'k8s/20-backend.yaml'
Assert-Matches $plainManifest 'AgentHub__ClaudeAgentImage:\s*"registry\.example\.com/agenthub/agent-runtime-claude:latest"' 'Plain Kubernetes manifest must expose the Claude runtime image.'
Assert-Matches $plainManifest 'AgentHub__CodexAgentImage:\s*"registry\.example\.com/agenthub/agent-runtime-codex:latest"' 'Plain Kubernetes manifest must expose the Codex runtime image.'

$buildWorkflow = Read-RepoFile '.github/workflows/build-images.yml'
foreach ($component in @('backend', 'frontend', 'agent-runtime-claude', 'agent-runtime-codex')) {
    Assert-Matches $buildWorkflow ([regex]::Escape("component: $component")) "Image workflow is missing the $component matrix entry."
}
Assert-Matches $buildWorkflow 'context:\s*\./agent-runtime[\s\S]*dockerfile:\s*\./agent-runtime/claude/Dockerfile' 'Image workflow must map Claude to the shared runtime context and Claude Dockerfile.'
Assert-Matches $buildWorkflow 'context:\s*\./agent-runtime[\s\S]*dockerfile:\s*\./agent-runtime/codex/Dockerfile' 'Image workflow must map Codex to the shared runtime context and Codex Dockerfile.'

$testWorkflow = Read-RepoFile '.github/workflows/test.yml'
Assert-Matches $testWorkflow 'working-directory:\s*agent-runtime/session-agent[\s\S]*npm test' 'Test workflow must run the full shared/Claude/Codex Node suite.'
Assert-Matches $testWorkflow 'agent-runtime/claude/Dockerfile' 'Test workflow must exercise the Claude Dockerfile.'
Assert-Matches $testWorkflow 'agent-runtime/codex/Dockerfile' 'Test workflow must exercise the Codex Dockerfile.'
Assert-Matches $testWorkflow 'tests/helm/codex-runtime-values\.ps1' 'Test workflow must run rendered Helm assertions.'
Assert-Matches $testWorkflow 'tests/helm/deployment-parity\.ps1' 'Test workflow must run deployment parity assertions.'

$deploymentFiles = @(
    $powerShellSetup,
    $bashSetup,
    $buildWorkflow,
    $testWorkflow,
    $devValues,
    (Read-RepoFile 'helm/open-agenthub/values.yaml'),
    (Read-RepoFile 'helm/open-agenthub/templates/_helpers.tpl'),
    (Read-RepoFile 'helm/open-agenthub/templates/configmap.yaml'),
    $plainManifest
) -join "`n"
Assert-NotMatches $deploymentFiles 'agent-runtime/Dockerfile' 'Deployment wiring still references the removed root runtime Dockerfile.'

Write-Output 'Local setup, Helm, plain manifest, and workflow parity assertions passed.'
