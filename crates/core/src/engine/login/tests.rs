use super::*;

fn engine() -> (tempfile::TempDir, Engine) {
    let dir = tempfile::tempdir().unwrap();
    let engine = Engine::init_isolated_demo(dir.path()).unwrap();
    (dir, engine)
}

fn credential(token: &str) -> String {
    json!({"claudeAiOauth": {"accessToken": token, "refreshToken": format!("refresh-{token}")}})
        .to_string()
}

fn config(email: &str, org: &str) -> String {
    json!({"oauthAccount": {"emailAddress": email, "accountUuid": format!("uuid-{email}"),
        "organizationUuid": org}, "customSetting": "keep"})
    .to_string()
}

fn live(engine: &Engine, email: &str, org: &str, token: &str) {
    fs::write(engine.paths.env.credentials_path(), credential(token)).unwrap();
    fs::write(&engine.paths.global_config, config(email, org)).unwrap();
}

fn stage(flow: &Value, email: &str, org: &str, token: &str) -> Value {
    let dir = PathBuf::from(flow["configDir"].as_str().unwrap());
    fs::write(dir.join(".credentials.json"), credential(token)).unwrap();
    fs::write(dir.join(".claude.json"), config(email, org)).unwrap();
    json!({"id": flow["id"], "activate": true, "status": {"loggedIn": true,
        "authMethod": "claude.ai", "email": email, "orgId": org, "configDirectory": dir}})
}

fn begin(engine: &Engine) -> Value {
    engine.call_json("login_begin", &json!({})).unwrap()
}
fn cancel(engine: &Engine, flow: &Value) {
    engine
        .call_json("login_cancel", &json!({"id": flow["id"], "discard": true}))
        .unwrap();
}

#[test]
fn backup_precedes_login_and_cancel_keeps_current() {
    let (_dir, eng) = engine();
    live(&eng, "old@x.com", "org1", "secret-old");
    let before = fs::read(eng.paths.env.credentials_path()).unwrap();
    let flow = begin(&eng);
    assert_eq!(flow["backedUp"], true);
    assert!(!flow.to_string().contains("secret-old"));
    assert!(eng
        .switcher
        .store
        .read_slot(1, "old@x.com")
        .unwrap()
        .unwrap()
        .contains("secret-old"));
    assert!(!Path::new(flow["configDir"].as_str().unwrap())
        .join(".credentials.json")
        .exists());
    // The sequence lock must be available throughout the browser wait.
    let mut lock = FileLock::new(&eng.paths.lock_file).with_timeout(Duration::ZERO);
    lock.acquire().unwrap();
    drop(lock);
    assert!(eng.call_json("agent_run_list", &json!({})).is_ok());
    assert!(eng.call_json("get_settings", &json!({})).is_ok());
    for method in [
        "switch_to",
        "add_current",
        "remove_account",
        "refresh_usage",
        "autoswitch_tick",
        "purge_apply",
        "relocate_apply",
        "login_begin",
    ] {
        assert!(eng
            .call_json(method, &json!({}))
            .unwrap_err()
            .to_string()
            .contains("login-in-progress"));
    }
    cancel(&eng, &flow);
    assert_eq!(fs::read(eng.paths.env.credentials_path()).unwrap(), before);
    assert!(!Path::new(flow["configDir"].as_str().unwrap()).exists());
}

#[test]
fn successful_login_saves_both_accounts_and_preserves_settings() {
    let (_dir, eng) = engine();
    live(&eng, "old@x.com", "org1", "old");
    let flow = begin(&eng);
    let params = stage(&flow, "new@x.com", "org2", "new");
    let result = eng.call_json("login_commit", &params).unwrap();
    assert_eq!(result["switched"], true);
    assert_eq!(result["number"], 2);
    let seq = eng.switcher.load_sequence().unwrap();
    assert_eq!(seq.sequence.len(), 2);
    assert_eq!(seq.active_account_number, Some(2));
    assert_eq!(
        crate::oauth::access_token(&eng.switcher.store.read_active().unwrap().unwrap()).as_deref(),
        Some("new")
    );
    let cfg: Value =
        serde_json::from_str(&fs::read_to_string(&eng.paths.global_config).unwrap()).unwrap();
    assert_eq!(cfg["customSetting"], "keep");
    assert!(!Path::new(flow["configDir"].as_str().unwrap()).exists());
}

#[test]
fn same_account_refresh_keeps_metadata_and_the_new_token() {
    let (_dir, eng) = engine();
    live(&eng, "old@x.com", "org1", "old");
    eng.add_current(None, Some("My alias".into())).unwrap();
    let mut seq = eng.switcher.load_sequence().unwrap();
    let rec = seq.account_mut(1).unwrap();
    rec.disabled = true;
    rec.proxy = Some("direct".into());
    seq.save(&eng.paths.sequence_file).unwrap();
    let flow = begin(&eng);
    let result = eng
        .call_json("login_commit", &stage(&flow, "old@x.com", "org1", "fresh"))
        .unwrap();
    assert_eq!(result["existing"], true);
    assert_eq!(result["switched"], true);
    let seq = eng.switcher.load_sequence().unwrap();
    assert_eq!(seq.sequence.len(), 1);
    let rec = seq.account(1).unwrap();
    assert_eq!(rec.alias.as_deref(), Some("My alias"));
    assert!(rec.disabled);
    assert_eq!(rec.proxy.as_deref(), Some("direct"));
    assert!(eng
        .switcher
        .store
        .read_slot(1, &rec.email)
        .unwrap()
        .unwrap()
        .contains("fresh"));
}

#[test]
fn one_person_in_two_organizations_gets_distinct_slots() {
    let (_dir, eng) = engine();
    live(&eng, "same@x.com", "org1", "old");
    let flow = begin(&eng);
    let result = eng
        .call_json("login_commit", &stage(&flow, "same@x.com", "org2", "new"))
        .unwrap();
    assert_eq!(result["existing"], false);
    assert_eq!(eng.switcher.load_sequence().unwrap().sequence.len(), 2);
    assert_eq!(eng.snapshot().unwrap().active_account_number, Some(2));
    let next = begin(&eng);
    assert_eq!(eng.switcher.load_sequence().unwrap().sequence.len(), 2);
    cancel(&eng, &next);
}

#[test]
fn external_change_prevents_activation_but_still_saves_new_account() {
    let (_dir, eng) = engine();
    live(&eng, "old@x.com", "org1", "old");
    let flow = begin(&eng);
    let params = stage(&flow, "new@x.com", "org2", "new");
    live(&eng, "external@x.com", "org3", "external");
    let result = eng.call_json("login_commit", &params).unwrap();
    assert_eq!(result["currentChanged"], true);
    assert_eq!(result["switched"], false);
    assert!(eng
        .switcher
        .store
        .read_active()
        .unwrap()
        .unwrap()
        .contains("external"));
    assert!(eng
        .switcher
        .store
        .read_slot(2, "new@x.com")
        .unwrap()
        .is_some());
}

#[test]
fn first_account_can_be_saved_without_becoming_active() {
    let (_dir, eng) = engine();
    let flow = begin(&eng);
    assert_eq!(flow["backedUp"], false);
    let mut params = stage(&flow, "new@x.com", "org2", "new");
    params["activate"] = json!(false);
    let result = eng.call_json("login_commit", &params).unwrap();
    assert_eq!(result["switched"], false);
    assert!(eng.switcher.store.read_active().unwrap().is_none());
    assert_eq!(
        eng.switcher.load_sequence().unwrap().active_account_number,
        None
    );
}

#[test]
fn invalid_identity_and_wrong_auth_method_cannot_import() {
    let (_dir, eng) = engine();
    let flow = begin(&eng);
    let params = stage(&flow, "new@x.com", "org2", "new");
    for (field, value) in [
        ("email", json!("wrong@x.com")),
        ("authMethod", json!("api_key")),
        ("orgId", json!("wrong")),
        ("loggedIn", json!(false)),
        ("configDirectory", json!("C:/wrong")),
    ] {
        let mut bad = params.clone();
        bad["status"][field] = value;
        assert!(eng.call_json("login_commit", &bad).is_err());
        assert!(eng.switcher.load_sequence().unwrap().sequence.is_empty());
    }
    let bad = stage(&flow, "../escape@x.com", "org2", "new");
    assert!(eng.call_json("login_commit", &bad).is_err());
    cancel(&eng, &flow);
}

#[test]
fn backup_failure_never_creates_a_login_flow() {
    let (_dir, eng) = engine();
    live(&eng, "old@x.com", "org1", "old");
    fs::create_dir_all(&eng.paths.backup_root).unwrap();
    fs::write(&eng.paths.credentials_dir, "blocked").unwrap();
    assert!(eng.call_json("login_begin", &json!({})).is_err());
    assert!(eng.login.lock().is_none());
    assert!(!eng.login_root().exists());
    assert!(eng
        .switcher
        .store
        .read_active()
        .unwrap()
        .unwrap()
        .contains("old"));
}

#[test]
fn unreadable_current_login_is_not_treated_as_signed_out() {
    let (_dir, eng) = engine();
    fs::write(eng.paths.env.credentials_path(), "not-json-secret").unwrap();
    let err = eng
        .call_json("login_begin", &json!({}))
        .unwrap_err()
        .to_string();
    assert!(!err.contains("not-json-secret"));
    assert!(err.contains("login-current-unreadable"));
    assert!(eng.login.lock().is_none());
}

#[test]
fn interrupted_flow_is_recoverable_after_engine_restart_without_activation() {
    let (dir, eng) = engine();
    let flow = begin(&eng);
    let params = stage(&flow, "new@x.com", "org2", "new");
    drop(eng); // Simulate the app closing before import; no global login changed.
    let eng = Engine::init_isolated_demo(dir.path()).unwrap();
    assert_eq!(
        eng.login_pending().unwrap()["pending"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
    let recovered = eng
        .call_json("login_begin", &json!({"id": flow["id"]}))
        .unwrap();
    assert_eq!(recovered["recovered"], true);
    let result = eng.call_json("login_commit", &params).unwrap();
    assert_eq!(result["switched"], false);
    assert_eq!(result["currentChanged"], true);
    assert!(eng.switcher.store.read_active().unwrap().is_none());
}

#[cfg(windows)]
#[test]
fn activation_failure_restores_absent_credentials_and_keeps_import() {
    let (_dir, eng) = engine();
    fs::write(&eng.paths.global_config, "{}").unwrap();
    let flow = begin(&eng);
    let params = stage(&flow, "new@x.com", "org2", "new");
    let original = fs::metadata(&eng.paths.global_config)
        .unwrap()
        .permissions();
    let mut readonly = original.clone();
    readonly.set_readonly(true);
    fs::set_permissions(&eng.paths.global_config, readonly).unwrap();
    let result = eng.call_json("login_commit", &params);
    fs::set_permissions(&eng.paths.global_config, original).unwrap();
    let result = result.unwrap();
    assert_eq!(result["switched"], false);
    assert!(result["switchError"].is_string());
    assert!(!eng.paths.env.credentials_path().exists());
    assert_eq!(fs::read_to_string(&eng.paths.global_config).unwrap(), "{}");
    assert!(eng
        .switcher
        .store
        .read_slot(1, "new@x.com")
        .unwrap()
        .is_some());
}

#[cfg(windows)]
#[test]
fn failed_import_restores_previous_slot_credentials() {
    let (_dir, eng) = engine();
    live(&eng, "old@x.com", "org1", "old");
    let flow = begin(&eng);
    let params = stage(&flow, "old@x.com", "org1", "fresh");
    let path = slot_config_path(&eng.paths.configs_dir, 1, "old@x.com");
    let original = fs::metadata(&path).unwrap().permissions();
    let mut readonly = original.clone();
    readonly.set_readonly(true);
    fs::set_permissions(&path, readonly).unwrap();
    let result = eng.call_json("login_commit", &params);
    fs::set_permissions(&path, original).unwrap();
    assert!(result.is_err());
    assert_eq!(
        crate::oauth::access_token(
            &eng.switcher
                .store
                .read_slot(1, "old@x.com")
                .unwrap()
                .unwrap()
        )
        .as_deref(),
        Some("old")
    );
    assert!(Path::new(flow["configDir"].as_str().unwrap())
        .join(".credentials.json")
        .exists());
    cancel(&eng, &flow);
}
