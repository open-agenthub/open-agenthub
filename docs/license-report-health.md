# Licence report health

The admin overview reloads the shared signed token before showing its expiry. Successful
reports clear the persisted last error and advance last_report_at; rejected responses,
invalid renewal tokens, missing tokens and network failures persist fixed diagnostic text
and a UTC timestamp. Shutdown cancellation is not recorded as a reporting failure.

The stop date is the verified token's validUntil, not last success plus 45 days: trials,
granted licences and service-issued renewal windows can differ. Subscription cancellation
and plan validity remain separate display fields. Warning starts ReportWarningDays before
expiry (default 7, constrained to 0–365); expired tokens also warn. The overview endpoint is
admin-only and the status is refreshed on opening the page or using Refresh license status.

Rejected: process-local error state disappears on restart and disagrees between replicas.
Rejected: storing remote response bodies or exception text risks displaying credentials.
Rejected: inferring a failure solely from last_report_at confuses a newly activated token
with a failed report. No external notifications are sent; the Admin license page owns this
warning, with the last error visible even before its warning window.
