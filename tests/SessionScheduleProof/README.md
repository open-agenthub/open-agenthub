# Scheduled session editing proof (#243)

The schedule belongs to the CronJob, whereas agent runtime settings are baked into its pod
 template. A partial merge patch changes the schedule in place before storing it. Recreating
 the CronJob was rejected because it risks active Jobs and their history; changing only the
 database was rejected because the controller would keep using the old schedule. Runtime-field
 edits remain rejected by SessionUpdateValidator.

New and edited schedules use Etc/UTC explicitly. nextRunAt is the next nominal UTC occurrence,
 not a guarantee: Forbid concurrency and cluster availability can delay or skip a run. The shared
 parser validates before resources or records change. It supports five-field cron and calendar
 aliases; day-of-month and day-of-week use the controller's OR semantics. Invalid legacy rows
 return no estimate rather than failing a list request.

A database failure after the cluster patch can leave the stored schedule stale; retrying the
 update reconciles it. No distributed transaction spans the API server and the database.
