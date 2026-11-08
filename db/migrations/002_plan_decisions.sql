-- Nexus migration 1 → 2 (ROADMAP.md, feature 2: deterministic plan engine).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 1.
-- New databases get the same result directly from schema.sql + security.sql + seed.sql.

-- Who rejected a plan and why, and why a plan failed.
ALTER TABLE plans ADD COLUMN rejected_by INTEGER REFERENCES users (id);
ALTER TABLE plans ADD COLUMN rejection_reason TEXT
    CHECK (rejection_reason IS NULL OR length(rejection_reason) BETWEEN 1 AND 200);
ALTER TABLE plans ADD COLUMN failure_reason TEXT;

-- schema.sql declares "a rejected plan has rejected_by and rejection_reason" as a table CHECK, which
-- ALTER TABLE cannot add; on migrated databases the same rule is enforced by these two triggers.
CREATE TRIGGER plans_require_rejection_details_insert BEFORE INSERT ON plans
WHEN new.status = 'rejected' AND (new.rejected_by IS NULL OR new.rejection_reason IS NULL) BEGIN
    SELECT RAISE(ABORT, 'DENY: a rejected plan needs rejected_by and rejection_reason');
END;

CREATE TRIGGER plans_require_rejection_details_update BEFORE UPDATE ON plans
WHEN new.status = 'rejected' AND (new.rejected_by IS NULL OR new.rejection_reason IS NULL) BEGIN
    SELECT RAISE(ABORT, 'DENY: a rejected plan needs rejected_by and rejection_reason');
END;

-- Same rules as security.sql (version 2) for rejections.
CREATE TRIGGER plans_deny_ai_rejection BEFORE UPDATE OF rejected_by ON plans
WHEN new.rejected_by IS NOT NULL
 AND (SELECT r.is_ai FROM users u JOIN roles r ON r.id = u.role_id WHERE u.id = new.rejected_by) = 1 BEGIN
    SELECT RAISE(ABORT, 'DENY: the AI cannot reject plans');
END;

CREATE TRIGGER plans_deny_self_rejection BEFORE UPDATE OF rejected_by ON plans
WHEN new.rejected_by IS NOT NULL AND new.rejected_by = new.requested_by BEGIN
    SELECT RAISE(ABORT, 'DENY: a plan cannot be rejected by its requester');
END;

CREATE TRIGGER plans_deny_approve_and_reject BEFORE UPDATE ON plans
WHEN new.approved_by IS NOT NULL AND new.rejected_by IS NOT NULL BEGIN
    SELECT RAISE(ABORT, 'DENY: a plan cannot be both approved and rejected');
END;

-- The demo password of the fictitious seed users changed from nexus-demo-2026 to demo. Only accounts
-- still holding the exact version-1 seed hash are updated; a password someone changed is left untouched.
UPDATE users SET password_hash = '$argon2id$v=19$m=65536,t=3,p=4$nqN+GxHYJbAl3p4JDrtF0w$6vn3eeADPWe1PZ/tcMhn7vBpk9VMzBgNneefuOBzsBE'
    WHERE username = 'op.joao' AND password_hash = '$argon2id$v=19$m=65536,t=3,p=4$4H3UK/Bmc+aoDlJBaqyenA$qVxvHSzAzpt4OnIntR3nD14T/SSD/TSRo8ARXm5bBeU';
UPDATE users SET password_hash = '$argon2id$v=19$m=65536,t=3,p=4$LHTzEHWBbd2e+1nLzx77OQ$pI8lj+Ak2xPg/z8VOXLr8dlbL+7O/nFUDUMfCllqBz4'
    WHERE username = 'sup.maria' AND password_hash = '$argon2id$v=19$m=65536,t=3,p=4$qDb4i5a/Q7pRKB42TQvh7w$74hTtoahWJTTZBGVt9YVSrP/0x9IAX6vNU5PALEeOsY';
UPDATE users SET password_hash = '$argon2id$v=19$m=65536,t=3,p=4$rWfBWwMAcH6POiYYHyzQYw$McBlLHK9i8Uq1PTjsNuGYJvbqBT0lvQ883q5q/4BlS8'
    WHERE username = 'adm.carlos' AND password_hash = '$argon2id$v=19$m=65536,t=3,p=4$u6+exbn+H/J0CbgXzHqQEw$P0rgm1gX3qvwNfWyTdpvZxeE8y5m97oCUwztYYvS9pk';
UPDATE users SET password_hash = '$argon2id$v=19$m=65536,t=3,p=4$3yuFBv8plrBH67sXnZd8Sg$vwZLCHQ2VNDUjlLH25zIpurlFbvB3s9obrcfwFh23m4'
    WHERE username = 'aud.lucia' AND password_hash = '$argon2id$v=19$m=65536,t=3,p=4$LCPw3cwEL2flFKGWlwGyBw$gc8yzrgocnj440mK4A6E76EjX/64uAT8rkpzLrDISXs';
