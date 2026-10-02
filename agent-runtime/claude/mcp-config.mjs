import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

/**
 * Registers the session's MCP servers in Claude Code's *user* scope — `mcpServers` at the top level
 * of `~/.claude.json`.
 *
 * This replaces a copy of the effective config at `$AGENTHUB_WORKDIR/.mcp.json`. That location was
 * wrong twice over. With a single repository the working directory is the clone, so the file was an
 * untracked artifact in a tree the agent is about to commit. And it did not work: verified against
 * the pinned CLI (2.1.283), a server from a project `.mcp.json` is reported as
 * "⏸ Pending approval (run `claude` to approve)" and is never connected to, which is exactly what
 * an unattended session cannot provide. A user-scoped server is health-checked and connected
 * straight away, from any working directory.
 *
 * The session's own agent is still launched with `--mcp-config`, which is unchanged and remains the
 * authoritative path. The user scope is what makes the same servers available to anything else that
 * runs `claude` inside the session, where no flag is passed. Having a server in both places is not
 * a conflict: with a name present in both, the CLI gets past configuration parsing to the auth
 * check, while a genuinely malformed config fails before it with "Invalid MCP configuration".
 *
 * The other runtimes need no equivalent — Codex reads `$CODEX_HOME/config.toml` and Cursor reads
 * `$CURSOR_CONFIG_DIR/mcp.json`, both already written by their own entrypoints, and neither looks
 * at `.mcp.json` at all.
 */

/** Private key recording which server names this module put there, so they can be withdrawn. */
export const MANAGED_KEY = 'agenthubManagedMcpServers';

function readJson(path) {
  if (!fs.existsSync(path)) return {};
  try {
    const parsed = JSON.parse(fs.readFileSync(path, 'utf8'));
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  } catch { return {}; }
}

function serversFrom(mcpConfigPath) {
  if (!mcpConfigPath) return {};
  const servers = readJson(mcpConfigPath).mcpServers;
  return servers && typeof servers === 'object' && !Array.isArray(servers) ? servers : {};
}

/**
 * Merges the session's servers into an existing config object.
 *
 * Previously managed names are withdrawn first, tracked under {@link MANAGED_KEY} rather than by
 * replacing `mcpServers` wholesale: a custom image may ship user-scoped servers of its own, and a
 * server dropped from the session's config has to disappear rather than linger from an earlier
 * start — the state directory is restored from the session's own tar, so `~/.claude.json` comes
 * back with whatever the last incarnation wrote.
 */
export function mergeMcpServers(config, servers) {
  const previous = Array.isArray(config?.[MANAGED_KEY]) ? config[MANAGED_KEY] : [];
  const existing = config?.mcpServers && typeof config.mcpServers === 'object'
    && !Array.isArray(config.mcpServers) ? config.mcpServers : {};

  const kept = Object.fromEntries(
    Object.entries(existing).filter(([name]) => !previous.includes(name)));
  const names = Object.keys(servers);
  const merged = { ...config, mcpServers: { ...kept, ...servers } };

  if (names.length === 0) {
    delete merged[MANAGED_KEY];
    if (Object.keys(merged.mcpServers).length === 0) delete merged.mcpServers;
  } else {
    merged[MANAGED_KEY] = names;
  }
  return merged;
}

export function writeMcpConfig(configPath, mcpConfigPath) {
  const merged = mergeMcpServers(readJson(configPath), serversFrom(mcpConfigPath));
  fs.writeFileSync(configPath, `${JSON.stringify(merged)}\n`, { mode: 0o600 });
  return configPath;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeMcpConfig(process.argv[2], process.argv[3]);
