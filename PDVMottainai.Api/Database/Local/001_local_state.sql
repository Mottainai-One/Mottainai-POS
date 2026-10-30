PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS local_sequence (
    sequence_name TEXT PRIMARY KEY,
    next_value INTEGER NOT NULL
);

INSERT INTO local_sequence(sequence_name, next_value)
VALUES ('shift', -1), ('sale', -1), ('sale_item', -1)
ON CONFLICT(sequence_name) DO NOTHING;

CREATE TABLE IF NOT EXISTS offline_operator (
    firebase_uid TEXT PRIMARY KEY,
    employee_id INTEGER NOT NULL,
    store_id INTEGER NOT NULL,
    terminal_id INTEGER NOT NULL,
    profile_ciphertext BLOB NOT NULL,
    permissions_ciphertext BLOB NOT NULL,
    offline_grant_ciphertext BLOB NOT NULL,
    grant_expires_at TEXT NOT NULL,
    source_session_id TEXT NOT NULL,
    cached_at TEXT NOT NULL,
    revoked_at TEXT
);

CREATE TABLE IF NOT EXISTS local_access_session (
    access_token_hash TEXT PRIMARY KEY,
    refresh_token_hash TEXT NOT NULL UNIQUE,
    firebase_uid TEXT NOT NULL REFERENCES offline_operator(firebase_uid),
    expires_at TEXT NOT NULL,
    refresh_expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    revoked_at TEXT
);

CREATE TABLE IF NOT EXISTS terminal_cache (
    terminal_id INTEGER PRIMARY KEY,
    store_id INTEGER NOT NULL,
    terminal_code TEXT NOT NULL,
    name TEXT NOT NULL,
    active INTEGER NOT NULL,
    cached_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS product_cache (
    product_id INTEGER PRIMARY KEY,
    sku TEXT NOT NULL,
    barcode TEXT,
    name TEXT NOT NULL,
    unit_measure TEXT NOT NULL,
    normal_price TEXT NOT NULL,
    active INTEGER NOT NULL,
    version INTEGER NOT NULL,
    cached_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_product_cache_barcode ON product_cache(barcode);
CREATE INDEX IF NOT EXISTS ix_product_cache_name ON product_cache(name);
CREATE INDEX IF NOT EXISTS ix_product_cache_sku ON product_cache(sku);

CREATE TABLE IF NOT EXISTS customer_lookup_cache (
    auth_token_hash TEXT PRIMARY KEY,
    customer_id INTEGER NOT NULL,
    display_ciphertext BLOB NOT NULL,
    cached_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS local_shift (
    local_shift_id INTEGER PRIMARY KEY,
    remote_shift_id INTEGER,
    terminal_id INTEGER NOT NULL,
    employee_id INTEGER NOT NULL,
    opened_at TEXT NOT NULL,
    opening_amount TEXT NOT NULL,
    closed_at TEXT,
    closing_amount TEXT,
    expected_amount TEXT,
    status TEXT NOT NULL,
    observation_ciphertext BLOB,
    sync_state TEXT NOT NULL CHECK(sync_state IN ('LOCAL','PENDING','SYNCED','CONFLICT')),
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS local_cash_movement (
    local_movement_id INTEGER PRIMARY KEY AUTOINCREMENT,
    remote_movement_id INTEGER,
    shift_id INTEGER NOT NULL,
    employee_id INTEGER NOT NULL,
    movement_type TEXT NOT NULL CHECK(movement_type IN ('SANGRIA','SUPRIMENTO')),
    amount TEXT NOT NULL,
    reason_ciphertext BLOB NOT NULL,
    observation_ciphertext BLOB,
    movement_date TEXT NOT NULL,
    sync_state TEXT NOT NULL CHECK(sync_state IN ('LOCAL','PENDING','SYNCED','CONFLICT')),
    FOREIGN KEY(shift_id) REFERENCES local_shift(local_shift_id)
);

CREATE TABLE IF NOT EXISTS local_sale (
    local_sale_id INTEGER PRIMARY KEY,
    remote_sale_id INTEGER,
    sale_date TEXT NOT NULL,
    store_id INTEGER NOT NULL,
    employee_id INTEGER NOT NULL,
    shift_id INTEGER NOT NULL,
    customer_id INTEGER,
    status TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    observation_ciphertext BLOB,
    sync_state TEXT NOT NULL CHECK(sync_state IN ('LOCAL','PENDING','SYNCED','CONFLICT')),
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS local_sale_item (
    local_sale_item_id INTEGER PRIMARY KEY,
    remote_sale_item_id INTEGER,
    sale_id INTEGER NOT NULL,
    product_id INTEGER NOT NULL,
    quantity TEXT NOT NULL,
    unit_price TEXT NOT NULL,
    sync_state TEXT NOT NULL CHECK(sync_state IN ('LOCAL','PENDING','SYNCED','CONFLICT')),
    updated_at TEXT NOT NULL,
    FOREIGN KEY(sale_id) REFERENCES local_sale(local_sale_id)
);

CREATE TABLE IF NOT EXISTS id_mapping (
    entity_type TEXT NOT NULL,
    local_id INTEGER NOT NULL,
    remote_id INTEGER NOT NULL,
    remote_partition_date TEXT,
    PRIMARY KEY(entity_type, local_id)
);

CREATE TABLE IF NOT EXISTS outbox (
    sequence_id INTEGER PRIMARY KEY AUTOINCREMENT,
    operation_id TEXT NOT NULL UNIQUE,
    idempotency_key TEXT NOT NULL UNIQUE,
    terminal_id INTEGER NOT NULL,
    entity_type TEXT NOT NULL,
    local_entity_id INTEGER NOT NULL,
    operation_type TEXT NOT NULL,
    payload_ciphertext BLOB NOT NULL,
    payload_hash TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'PENDING'
        CHECK(state IN ('PENDING','PROCESSING','SYNCED','CONFLICT')),
    attempts INTEGER NOT NULL DEFAULT 0,
    last_error_ciphertext BLOB,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_outbox_pending
    ON outbox(state, sequence_id);
