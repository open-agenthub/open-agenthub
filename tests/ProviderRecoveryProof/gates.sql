CREATE TABLE provider_recovery_proof_gates (
    request_id UUID NOT NULL, sequence INTEGER PRIMARY KEY, phase TEXT NOT NULL,
    stop_ack BOOLEAN, released BOOLEAN NOT NULL DEFAULT FALSE,
    aborted BOOLEAN NOT NULL DEFAULT FALSE, handoff_released BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE FUNCTION provider_recovery_proof_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE gate_sequence INTEGER;
BEGIN
    SELECT sequence INTO gate_sequence FROM provider_recovery_proof_gates
    WHERE request_id=NEW.request_id AND phase=NEW.phase AND NOT released
        AND (stop_ack IS NULL OR stop_ack=COALESCE((NEW.progress->>'sourceStopAcknowledged')::boolean,FALSE))
    ORDER BY sequence LIMIT 1;
    IF gate_sequence IS NOT NULL THEN
        -- An advisory lock is observable from another connection before this write commits.
        -- Killing that backend must leave the previously saved phase intact.
        PERFORM pg_advisory_xact_lock(529,gate_sequence);
        WHILE EXISTS(SELECT 1 FROM provider_recovery_proof_gates WHERE sequence=gate_sequence AND NOT released) LOOP
            IF EXISTS(SELECT 1 FROM provider_recovery_proof_gates WHERE sequence=gate_sequence AND aborted) THEN
                RAISE EXCEPTION 'external proof transaction aborted';
            END IF;
            PERFORM pg_sleep(0.1);
        END LOOP;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER provider_recovery_proof_gate BEFORE UPDATE ON session_provider_switches
FOR EACH ROW EXECUTE FUNCTION provider_recovery_proof_barrier();
