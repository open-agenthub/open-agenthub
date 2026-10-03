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

if (($powerShellSetup | Select-String -Pattern '(?m)^docker build ' -AllMatches).Matches.Count -ne 7) {
    throw 'setup-dev.ps1 must build exactly seven development images.'
}
if (($bashSetup | Select-String -Pattern '(?m)^docker build ' -AllMatches).Matches.Count -ne 7) {
    throw 'setup-dev.sh must build exactly seven development images.'
}

$checkedPowerShellBuilds = [regex]::Matches(
    $powerShellSetup,
    "(?m)^docker build [^\r\n]+\r?\nAssert-NativeSuccess '[^']+ image build'\s*$"
)
if ($checkedPowerShellBuilds.Count -ne 7) {
    throw 'setup-dev.ps1 must stop immediately when any development image build fails.'
}

foreach ($requiredOperation in @(
    'Helm deployment',
    'Postgres rollout',
    'Backend rollout restart',
    'Backend rollout',
    'Frontend rollout'
)) {
    if ($powerShellSetup -notmatch "Assert-NativeSuccess '$([regex]::Escape($requiredOperation))'") {
        throw "setup-dev.ps1 must stop when the $requiredOperation command fails."
    }
}

foreach ($setup in @($powerShellSetup, $bashSetup)) {
    Assert-Matches $setup 'open-agenthub-dev/backend:local' 'Local setup must build the backend image.'
    Assert-Matches $setup 'open-agenthub-dev/frontend:local' 'Local setup must build the frontend image.'
    Assert-Matches $setup 'open-agenthub-dev/agent-runtime-claude:local' 'Local setup must build the Claude runtime image.'
    Assert-Matches $setup 'open-agenthub-dev/agent-runtime-codex:local' 'Local setup must build the Codex runtime image.'
    Assert-Matches $setup 'open-agenthub-dev/agent-runtime-cursor:local' 'Local setup must build the Cursor runtime image.'
    Assert-Matches $setup 'open-agenthub-dev/agent-runtime-openclaw:local' 'Local setup must build the OpenClaw runtime image.'
    Assert-Matches $setup 'open-agenthub-dev/browser:local' 'Local setup must build the browser runtime image.'
    Assert-NotMatches $setup 'open-agenthub-dev/agent-runtime:local' 'Local setup still builds the removed legacy runtime image.'
}

Assert-Matches $powerShellSetup "'agent-runtime/claude/Dockerfile'.*'agent-runtime'" 'PowerShell setup must use the Claude Dockerfile with the agent-runtime context.'
Assert-Matches $powerShellSetup "'agent-runtime/codex/Dockerfile'.*'agent-runtime'" 'PowerShell setup must use the Codex Dockerfile with the agent-runtime context.'
Assert-Matches $powerShellSetup "'agent-runtime/cursor/Dockerfile'.*'agent-runtime'" 'PowerShell setup must use the Cursor Dockerfile with the agent-runtime context.'
Assert-Matches $powerShellSetup "'agent-runtime/openclaw/Dockerfile'.*'agent-runtime'" 'PowerShell setup must use the OpenClaw Dockerfile with the agent-runtime context.'
Assert-Matches $bashSetup 'agent-runtime/claude/Dockerfile" --tag .*agent-runtime-claude:local.*"\$script_dir/agent-runtime"' 'Bash setup must use the Claude Dockerfile with the agent-runtime context.'
Assert-Matches $bashSetup 'agent-runtime/codex/Dockerfile" --tag .*agent-runtime-codex:local.*"\$script_dir/agent-runtime"' 'Bash setup must use the Codex Dockerfile with the agent-runtime context.'
Assert-Matches $bashSetup 'agent-runtime/cursor/Dockerfile" --tag .*agent-runtime-cursor:local.*"\$script_dir/agent-runtime"' 'Bash setup must use the Cursor Dockerfile with the agent-runtime context.'
Assert-Matches $bashSetup 'agent-runtime/openclaw/Dockerfile" --tag .*agent-runtime-openclaw:local.*"\$script_dir/agent-runtime"' 'Bash setup must use the OpenClaw Dockerfile with the agent-runtime context.'

Assert-Matches $powerShellSetup '\$requiredContext = ''docker-desktop''' 'PowerShell setup lost the docker-desktop context requirement.'
Assert-Matches $bashSetup 'required_context=''docker-desktop''' 'Bash setup lost the docker-desktop context requirement.'
Assert-Matches $powerShellSetup 'Refusing to deploy: kubectl context' 'PowerShell setup lost the wrong-context refusal.'
Assert-Matches $bashSetup 'Refusing to deploy: kubectl context' 'Bash setup lost the wrong-context refusal.'

$devValues = Read-RepoFile 'helm/open-agenthub/values-dev.yaml'
Assert-Matches $devValues 'claude:\s*open-agenthub-dev/agent-runtime-claude:local' 'Development Helm values must select the locally built Claude runtime.'
Assert-Matches $devValues 'codex:\s*open-agenthub-dev/agent-runtime-codex:local' 'Development Helm values must select the locally built Codex runtime.'
Assert-Matches $devValues 'cursor:\s*open-agenthub-dev/agent-runtime-cursor:local' 'Development Helm values must select the locally built Cursor runtime.'
Assert-Matches $devValues 'openclaw:\s*open-agenthub-dev/agent-runtime-openclaw:local' 'Development Helm values must select the locally built OpenClaw runtime.'
Assert-Matches $devValues 'repository:\s*open-agenthub-dev/browser[\s\S]*tag:\s*local' 'Development Helm values must select the locally built browser runtime.'

$plainManifest = Read-RepoFile 'k8s/20-backend.yaml'
Assert-Matches $plainManifest 'AgentHub__ClaudeAgentImage:\s*"registry\.example\.com/agenthub/agent-runtime-claude:latest"' 'Plain Kubernetes manifest must expose the Claude runtime image.'
Assert-Matches $plainManifest 'AgentHub__CodexAgentImage:\s*"registry\.example\.com/agenthub/agent-runtime-codex:latest"' 'Plain Kubernetes manifest must expose the Codex runtime image.'
Assert-Matches $plainManifest 'AgentHub__CursorAgentImage:\s*"registry\.example\.com/agenthub/agent-runtime-cursor:latest"' 'Plain Kubernetes manifest must expose the Cursor runtime image.'
Assert-Matches $plainManifest 'AgentHub__OpenClawAgentImage:\s*"registry\.example\.com/agenthub/agent-runtime-openclaw:latest"' 'Plain Kubernetes manifest must expose the OpenClaw runtime image.'
Assert-Matches $plainManifest 'Browser__Enabled:\s*"true"' 'Plain Kubernetes manifest must enable browser orchestration.'
Assert-Matches $plainManifest 'Browser__Image:\s*"registry\.example\.com/agenthub/browser:latest"' 'Plain Kubernetes manifest must expose the browser runtime image.'

$plainRbac = Read-RepoFile 'k8s/10-rbac.yaml'
Assert-Matches $plainRbac 'resources:\s*\["networkpolicies"\][\s\S]*verbs:\s*\["get", "list", "watch", "create", "delete"\]' 'Plain RBAC must grant only browser NetworkPolicy lifecycle verbs.'
foreach ($staticPolicy in @((Read-RepoFile 'helm/open-agenthub/templates/networkpolicy.yaml'), (Read-RepoFile 'k8s/30-networkpolicy.yaml'))) {
    Assert-NotMatches $staticPolicy 'port:\s*9222' 'Static broad agent egress must not expose browser CDP.'
}

$buildWorkflow = Read-RepoFile '.github/workflows/build-images.yml'
# Two matrices now, and a component has to appear in both. The build job produces one image
# per architecture and pushes it by digest only, so a component built but left out of the merge
# job yields no manifest and therefore no usable tag — a deploy would find nothing to pull.
# Checking only the build side would not catch that.
foreach ($component in @('backend', 'frontend', 'agent-runtime-claude', 'agent-runtime-codex', 'agent-runtime-cursor', 'agent-runtime-openclaw', 'browser')) {
    Assert-Matches $buildWorkflow ([regex]::Escape("- name: $component")) "Image workflow is missing the $component build matrix entry."
    Assert-Matches $buildWorkflow "(?m)^\s+- $([regex]::Escape($component))\s*$" "Image workflow is missing the $component manifest merge entry."
}
Assert-Matches $buildWorkflow 'context:\s*\./agent-runtime[\s\S]*dockerfile:\s*\./agent-runtime/claude/Dockerfile' 'Image workflow must map Claude to the shared runtime context and Claude Dockerfile.'
Assert-Matches $buildWorkflow 'context:\s*\./agent-runtime[\s\S]*dockerfile:\s*\./agent-runtime/codex/Dockerfile' 'Image workflow must map Codex to the shared runtime context and Codex Dockerfile.'
Assert-Matches $buildWorkflow 'context:\s*\./agent-runtime[\s\S]*dockerfile:\s*\./agent-runtime/cursor/Dockerfile' 'Image workflow must map Cursor to the shared runtime context and Cursor Dockerfile.'
Assert-Matches $buildWorkflow 'context:\s*\./agent-runtime[\s\S]*dockerfile:\s*\./agent-runtime/openclaw/Dockerfile' 'Image workflow must map OpenClaw to the shared runtime context and OpenClaw Dockerfile.'

$testWorkflow = Read-RepoFile '.github/workflows/test.yml'
Assert-Matches $testWorkflow 'working-directory:\s*agent-runtime/session-agent[\s\S]*npm test' 'Test workflow must run the full shared/Claude/Codex/Cursor/OpenClaw Node suite.'
Assert-Matches $testWorkflow 'agent-runtime/claude/Dockerfile' 'Test workflow must exercise the Claude Dockerfile.'
Assert-Matches $testWorkflow 'agent-runtime/codex/Dockerfile' 'Test workflow must exercise the Codex Dockerfile.'
Assert-Matches $testWorkflow 'agent-runtime/cursor/Dockerfile' 'Test workflow must exercise the Cursor Dockerfile.'
Assert-Matches $testWorkflow 'agent-runtime/openclaw/Dockerfile' 'Test workflow must exercise the OpenClaw Dockerfile.'
Assert-Matches $testWorkflow 'working-directory:\s*browser-runtime[\s\S]*npm test' 'Test workflow must run browser runtime unit tests.'
Assert-Matches $testWorkflow 'tests/helm/browser-values\.ps1' 'Test workflow must run browser Helm assertions.'
Assert-Matches $testWorkflow 'tests/helm/codex-runtime-values\.ps1' 'Test workflow must run rendered Helm assertions.'
Assert-Matches $testWorkflow 'tests/helm/cursor-runtime-values\.ps1' 'Test workflow must run Cursor Helm assertions.'
Assert-Matches $testWorkflow 'tests/helm/openclaw-runtime-values\.ps1' 'Test workflow must run OpenClaw Helm assertions.'
Assert-Matches $testWorkflow 'tests/helm/files-values\.ps1' 'Test workflow must run session file Helm assertions.'
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
