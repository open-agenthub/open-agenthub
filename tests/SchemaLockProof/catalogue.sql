-- The schema as a sorted, order-independent listing, so a database migrated from an old release
-- can be compared with a fresh one: ADD COLUMN appends, so column positions differ by design.
-- schema_scripts is left out; it records what ran, not what the stores use.
\pset format unaligned
\pset tuples_only on
SELECT 'column ' || c.relname || '.' || a.attname || ' ' || format_type(a.atttypid, a.atttypmod)
       || CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END
       || COALESCE(' DEFAULT ' || pg_get_expr(d.adbin, d.adrelid), '')
FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
  AND c.relname <> 'schema_scripts'
UNION ALL
SELECT 'index ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename <> 'schema_scripts'
UNION ALL
SELECT 'constraint ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid)
FROM pg_constraint WHERE connamespace = 'public'::regnamespace AND conrelid::regclass::text <> 'schema_scripts'
UNION ALL
SELECT 'trigger ' || pg_get_triggerdef(oid) FROM pg_trigger WHERE NOT tgisinternal
UNION ALL
SELECT 'function ' || proname || ' ' || md5(prosrc) FROM pg_proc WHERE pronamespace = 'public'::regnamespace
ORDER BY 1;
