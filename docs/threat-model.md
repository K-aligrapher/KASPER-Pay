# KASPER threat model (PRD §17)

| Threat | Mitigation in KASPER | Test |
|---|---|---|
| Prompt injection in Slack / Notion / GitHub / documents / MCP output | Content is wrapped as `<<… untrusted content>>` data; tools and targets come only from validated plans with allowlisted refs; the model cannot set approval | `PlanValidatorTests`, `Model_cannot_add_parameters_like_requiresApproval` |
| Model invents tools, paths, channels, repos | Registry allowlist; `PlanValidator` checks every tool, parameter, reference, folder/channel/repo | `Unknown_tool_is_rejected`, `Invented_folders_are_rejected`, `Bad_references_are_rejected` |
| Wrong/unreviewed save, post or task | Final Review with faithful preview; approval bound to an action hash re-checked before acting | `File_changed_after_approval_is_caught_before_saving`, `Editing_invalidates_the_old_approval…` |
| Overwriting/escaping folders | Never overwrite (copy gets "(2)"), atomic temp+rename, canonical paths, junction check, system/KASPER folders can't be allowed | `Existing_files_are_never_overwritten`, `Unsafe_file_names_are_rejected`, `Folders_cannot_be_system_locations` |
| Duplicate writes after retries/crash | Idempotency keys; reconcile-before-retry for Notion, Slack, files | `External_write_interrupted_by_a_crash_is_replayed_without_duplicates` |
| Other local processes calling the API | Loopback only, random port, per-launch 256-bit token, Host-header check (DNS rebinding), session file ACL'd to the user | `Api_requires_token`, `Non_loopback_host_header_is_rejected`, `Approving_requires_the_session_token` |
| Stolen tokens | Notion/Slack/GitHub secrets encrypted with DPAPI (current user); never returned by the API or logged; least-privilege scopes documented | — |
| PDF passwords leaking | One-time in-memory secrets (`secret:…`); passed to QPDF via a deleted temp arg file; never in DB/logs; planner can't supply them | `Passwords_are_never_stored`, `Planner_cannot_supply_passwords` |
| Malicious documents / engine hangs | Engines run as child processes with timeouts, killed (whole tree) on cancel; isolated LibreOffice profile per job; upload type/size limits; executables refused | `Executables_cannot_be_uploaded`, `Timeouts_are_recorded` |
| Malicious MCP servers | Only servers you configure; only `AllowedTools`; everything needs approval unless `ReadOnlyTools`; output treated as untrusted | `Mcp_tools_need_approval_unless_marked_read_only` |
| Sensitive data in logs/traces | Logs hold categories and IDs, not prompts/content; trace view redacts secret-looking keys and truncates text; staging cleaned after commit/reject, failed staging after 7 days | — |

Known gaps: engines are not network-isolated or memory-capped; there is no multi-user auth (single local operator by design).
