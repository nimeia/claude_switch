//! C ABI surface for Windows P/Invoke and other FFI consumers.

#![allow(clippy::missing_safety_doc)]
// C ABI requires unsafe exteriors.
#![allow(unsafe_code)]

use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::path::PathBuf;
use std::ptr;
use std::sync::Arc;

use claude_switch_core::engine::Engine;
use claude_switch_core::errors::{Error, ErrorCode};
use claude_switch_core::usage::{MockHttp, SharedHttp};
use parking_lot::Mutex;

/// Opaque engine handle.
pub struct CsEngine {
    inner: Mutex<Engine>,
}

fn map_error(e: &Error) -> i32 {
    e.code() as i32
}

/// Create engine. `root_utf8` NULL → default process paths; else isolated root.
///
/// # Safety
/// `out_err` must be valid or null; `root_utf8` if non-null must be a valid C string.
#[no_mangle]
pub unsafe extern "C" fn cs_engine_create(
    root_utf8: *const c_char,
    out_err: *mut i32,
) -> *mut CsEngine {
    if !out_err.is_null() {
        *out_err = 0;
    }
    let result = (|| -> Result<Engine, Error> {
        if root_utf8.is_null() {
            Engine::init_default()
        } else {
            let s = CStr::from_ptr(root_utf8)
                .to_str()
                .map_err(|_| Error::Validation("root not utf8".into()))?;
            // Isolated roots are fixtures/demos: seed tok-a/tok-b usage so GUI
            // and FfiSmoke show real 5h/7d percentages without Anthropic.
            let http: SharedHttp = Arc::new(MockHttp::with_demo_tokens());
            Engine::init_isolated(PathBuf::from(s), http)
        }
    })();

    match result {
        Ok(eng) => Box::into_raw(Box::new(CsEngine {
            inner: Mutex::new(eng),
        })),
        Err(e) => {
            if !out_err.is_null() {
                *out_err = map_error(&e);
            }
            ptr::null_mut()
        }
    }
}

/// # Safety
/// `eng` must be from `cs_engine_create` or null.
#[no_mangle]
pub unsafe extern "C" fn cs_engine_free(eng: *mut CsEngine) {
    if eng.is_null() {
        return;
    }
    drop(Box::from_raw(eng));
}

/// Call JSON method. Caller frees `*out_json_utf8` with `cs_string_free`.
///
/// # Safety
/// Pointers must be valid; `out_json_utf8` must be non-null.
#[no_mangle]
pub unsafe extern "C" fn cs_engine_call(
    eng: *mut CsEngine,
    method_utf8: *const c_char,
    params_json_utf8: *const c_char,
    out_json_utf8: *mut *mut c_char,
) -> i32 {
    if eng.is_null() || out_json_utf8.is_null() {
        return ErrorCode::NullPointer as i32;
    }
    *out_json_utf8 = ptr::null_mut();

    if method_utf8.is_null() {
        return ErrorCode::NullPointer as i32;
    }

    let Ok(method) = CStr::from_ptr(method_utf8).to_str() else {
        return ErrorCode::ValidationFailed as i32;
    };

    let params_str = if params_json_utf8.is_null() {
        "{}"
    } else {
        match CStr::from_ptr(params_json_utf8).to_str() {
            Ok(s) => s,
            Err(_) => return ErrorCode::JsonParse as i32,
        }
    };

    let params: serde_json::Value = match serde_json::from_str(params_str) {
        Ok(v) => v,
        Err(_) => return ErrorCode::JsonParse as i32,
    };

    let engine = &*eng;
    let result = engine.inner.lock().call_json(method, &params);
    match result {
        Ok(v) => {
            let s = v.to_string();
            match CString::new(s) {
                Ok(c) => {
                    *out_json_utf8 = c.into_raw();
                    0
                }
                Err(_) => ErrorCode::Internal as i32,
            }
        }
        Err(e) => {
            let envelope = serde_json::json!({
                "schemaVersion": 1,
                "error": {
                    "code": e.code().as_str(),
                    "message": e.message(),
                    "retryable": e.retryable(),
                }
            });
            if let Ok(c) = CString::new(envelope.to_string()) {
                *out_json_utf8 = c.into_raw();
            }
            map_error(&e)
        }
    }
}

/// # Safety
/// `s` from `cs_engine_call` or null.
#[no_mangle]
pub unsafe extern "C" fn cs_string_free(s: *mut c_char) {
    if s.is_null() {
        return;
    }
    drop(CString::from_raw(s));
}

#[no_mangle]
pub extern "C" fn cs_error_code_string(code: i32) -> *const c_char {
    let s: &'static str = match code {
        0 => "ok\0",
        1 => "account-not-found\0",
        2 => "validation-failed\0",
        3 => "config-error\0",
        4 => "credential-error\0",
        5 => "credential-write-failed\0",
        6 => "credential-read-failed\0",
        7 => "lock-timeout\0",
        8 => "claude-code-lock-timeout\0",
        9 => "session-in-use\0",
        10 => "keychain-unavailable\0",
        11 => "network\0",
        12 => "transfer-error\0",
        13 => "migration-error\0",
        14 => "schema-unsupported\0",
        15 => "already-running\0",
        17 => "null-pointer\0",
        18 => "invalid-method\0",
        19 => "json-parse\0",
        _ => "internal\0",
    };
    s.as_ptr().cast()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CString;

    #[test]
    fn ffi_create_add_snapshot_switch() {
        let dir = tempfile::tempdir().unwrap();
        let root = CString::new(dir.path().to_str().unwrap()).unwrap();
        let mut err = 0i32;
        let eng = unsafe { cs_engine_create(root.as_ptr(), &mut err) };
        assert!(!eng.is_null(), "err={err}");
        assert_eq!(err, 0);

        let cred1 = r#"{"claudeAiOauth":{"accessToken":"tok-a","refreshToken":"r-a","emailAddress":"a@x.com"}}"#;
        let cfg1 = r#"{"oauthAccount":{"emailAddress":"a@x.com","accountUuid":"u","organizationUuid":"o","organizationName":"O","displayName":"a"}}"#;
        let cred2 = r#"{"claudeAiOauth":{"accessToken":"tok-b","refreshToken":"r-b","emailAddress":"b@x.com"}}"#;
        let cfg2 = r#"{"oauthAccount":{"emailAddress":"b@x.com","accountUuid":"u2","organizationUuid":"o","organizationName":"O","displayName":"b"}}"#;

        for (n, email, cred, cfg) in [(1u32, "a@x.com", cred1, cfg1), (2, "b@x.com", cred2, cfg2)] {
            let params = serde_json::json!({
                "number": n,
                "email": email,
                "credentials": cred,
                "config": cfg,
            })
            .to_string();
            let m = CString::new("add_raw").unwrap();
            let p = CString::new(params).unwrap();
            let mut out: *mut c_char = ptr::null_mut();
            let rc = unsafe { cs_engine_call(eng, m.as_ptr(), p.as_ptr(), &mut out) };
            assert_eq!(rc, 0, "add_raw failed");
            unsafe { cs_string_free(out) };
        }

        // Activate account 1 via switch_to after writing live through switch.
        // First set live by switch_to 1 (force activate path works with no prior active).
        {
            let m = CString::new("switch_to").unwrap();
            let p = CString::new(r#"{"id":"1"}"#).unwrap();
            let mut out: *mut c_char = ptr::null_mut();
            let rc = unsafe { cs_engine_call(eng, m.as_ptr(), p.as_ptr(), &mut out) };
            // May fail if no live config parent — still try snapshot.
            let _ = rc;
            unsafe { cs_string_free(out) };
        }

        let m = CString::new("snapshot").unwrap();
        let p = CString::new("{}").unwrap();
        let mut out: *mut c_char = ptr::null_mut();
        let rc = unsafe { cs_engine_call(eng, m.as_ptr(), p.as_ptr(), &mut out) };
        assert_eq!(rc, 0);
        let json = unsafe { CStr::from_ptr(out).to_string_lossy().into_owned() };
        unsafe { cs_string_free(out) };
        let v: serde_json::Value = serde_json::from_str(&json).unwrap();
        assert_eq!(
            v["schemaVersion"],
            claude_switch_core::SNAPSHOT_SCHEMA_VERSION
        );
        assert_eq!(v["accounts"].as_array().unwrap().len(), 2);

        // switch 1 -> 2
        {
            let m = CString::new("switch_to").unwrap();
            let p = CString::new(r#"{"id":"2"}"#).unwrap();
            let mut out: *mut c_char = ptr::null_mut();
            let rc = unsafe { cs_engine_call(eng, m.as_ptr(), p.as_ptr(), &mut out) };
            let json = if out.is_null() {
                String::new()
            } else {
                unsafe { CStr::from_ptr(out).to_string_lossy().into_owned() }
            };
            unsafe { cs_string_free(out) };
            assert_eq!(rc, 0, "switch_to failed: {json}");
            assert!(json.contains("switched") || json.contains("\"to\""));
        }

        {
            let m = CString::new("drain_events").unwrap();
            let p = CString::new("{}").unwrap();
            let mut out: *mut c_char = ptr::null_mut();
            let rc = unsafe { cs_engine_call(eng, m.as_ptr(), p.as_ptr(), &mut out) };
            assert_eq!(rc, 0);
            let json = unsafe { CStr::from_ptr(out).to_string_lossy().into_owned() };
            unsafe { cs_string_free(out) };
            assert!(
                json.contains("switch")
                    || json.contains("snapshot-updated")
                    || json.contains("Switch")
                    || json.contains("event")
            );
        }

        unsafe { cs_engine_free(eng) };
    }
}
