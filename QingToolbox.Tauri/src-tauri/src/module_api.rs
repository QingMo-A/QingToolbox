use serde_json::Value;

/// Compatibility version of the host capabilities exposed to process modules.
/// This is independent of the JSON wire envelope's `protocolVersion` and the
/// package profile string `moduleApiVersion` in qmod.json.
pub const API_VERSION: u32 = 1;

/// Process modules shipped before `apiVersion` existed used the v1 contract.
/// Keep them loadable; new packages must declare the version explicitly.
pub fn requested_version(value: Option<&Value>) -> Result<u32, &'static str> {
    let Some(value) = value else {
        return Ok(API_VERSION);
    };
    let Some(version) = value.as_u64().and_then(|number| u32::try_from(number).ok()) else {
        return Err("apiVersionInvalid");
    };
    if version == 0 {
        return Err("apiVersionInvalid");
    }
    Ok(version)
}

pub fn resolve_version(value: Option<&Value>) -> Result<u32, &'static str> {
    let version = requested_version(value)?;
    if version != API_VERSION {
        return Err("apiVersionUnsupported");
    }
    Ok(version)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_v1_and_legacy_absence_only() {
        assert_eq!(resolve_version(None), Ok(1));
        assert_eq!(resolve_version(Some(&serde_json::json!(1))), Ok(1));
        assert_eq!(
            resolve_version(Some(&serde_json::json!(0))),
            Err("apiVersionInvalid")
        );
        assert_eq!(
            resolve_version(Some(&serde_json::json!(1.0))),
            Err("apiVersionInvalid")
        );
        assert_eq!(
            resolve_version(Some(&serde_json::json!("1"))),
            Err("apiVersionInvalid")
        );
        assert_eq!(
            resolve_version(Some(&serde_json::json!(2))),
            Err("apiVersionUnsupported")
        );
    }
}
