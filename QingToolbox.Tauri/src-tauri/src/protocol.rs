use serde::{de::DeserializeOwned, Deserialize, Serialize};

/// The wire version is deliberately independent from the application version.
/// A module may evolve its implementation without silently changing the host
/// command/event contract.
pub const PROTOCOL_VERSION: u16 = 1;
pub const MAX_FRAME_BYTES: usize = 1024 * 1024;
const MAX_MESSAGE_TYPE_LENGTH: usize = 64;
const MAX_REQUEST_ID_LENGTH: usize = 128;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
#[serde(rename_all = "camelCase")]
pub struct ProtocolEnvelope<T> {
    pub protocol_version: u16,
    pub message_type: String,
    pub request_id: String,
    pub payload: T,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<ProtocolError>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
#[serde(rename_all = "camelCase")]
pub struct ProtocolError {
    pub code: String,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub details: Option<serde_json::Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProtocolValidationError {
    pub code: &'static str,
    pub message: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FrameError {
    pub code: &'static str,
    pub message: String,
}

impl<T> ProtocolEnvelope<T> {
    pub fn new(message_type: impl Into<String>, request_id: impl Into<String>, payload: T) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION,
            message_type: message_type.into(),
            request_id: request_id.into(),
            payload,
            error: None,
        }
    }

    /// Validate envelope metadata before crossing a process/window boundary.
    /// Payload-specific validation belongs to the command that owns the payload.
    pub fn validate(&self) -> Result<(), ProtocolValidationError> {
        if self.protocol_version != PROTOCOL_VERSION {
            return Err(ProtocolValidationError {
                code: "protocolVersionUnsupported",
                message: format!(
                    "Unsupported protocol version {}; expected {}.",
                    self.protocol_version, PROTOCOL_VERSION
                ),
            });
        }

        if !valid_token(&self.message_type, MAX_MESSAGE_TYPE_LENGTH) {
            return Err(ProtocolValidationError {
                code: "messageTypeInvalid",
                message: "messageType must be a short ASCII token.".to_string(),
            });
        }

        if !valid_token(&self.request_id, MAX_REQUEST_ID_LENGTH) {
            return Err(ProtocolValidationError {
                code: "requestIdInvalid",
                message: "requestId must be a short non-empty token.".to_string(),
            });
        }

        Ok(())
    }
}

/// Decode one newline-delimited JSON frame. The parser owns the byte and JSON
/// limits so every future sidecar/IPC transport gets the same boundary.
pub fn decode_line<T: DeserializeOwned>(frame: &[u8]) -> Result<ProtocolEnvelope<T>, FrameError> {
    if frame.len() > MAX_FRAME_BYTES {
        return Err(FrameError {
            code: "frameTooLarge",
            message: format!("IPC frame exceeds {MAX_FRAME_BYTES} bytes."),
        });
    }

    let text = std::str::from_utf8(frame).map_err(|_| FrameError {
        code: "frameNotUtf8",
        message: "IPC frame must be valid UTF-8 JSON.".to_string(),
    })?;
    let text = text
        .strip_suffix("\r\n")
        .or_else(|| text.strip_suffix('\n'))
        .unwrap_or(text);
    if text.is_empty() || text.contains(['\r', '\n']) {
        return Err(FrameError {
            code: "frameInvalid",
            message: "IPC frame must contain exactly one JSON line.".to_string(),
        });
    }

    let envelope =
        serde_json::from_str::<ProtocolEnvelope<T>>(text).map_err(|error| FrameError {
            code: "frameInvalidJson",
            message: format!("IPC frame is not valid JSON: {error}"),
        })?;
    envelope.validate().map_err(|error| FrameError {
        code: error.code,
        message: error.message,
    })?;
    Ok(envelope)
}

fn valid_token(value: &str, max_length: usize) -> bool {
    !value.is_empty()
        && value.len() <= max_length
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'-' | b'_' | b':'))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn envelope_round_trips_as_camel_case_json() {
        let envelope =
            ProtocolEnvelope::new("modules.list", "scan-1", serde_json::json!({ "count": 0 }));
        envelope.validate().expect("valid envelope");
        let json = serde_json::to_string(&envelope).expect("serialize envelope");
        assert!(json.contains("protocolVersion"));
        assert!(json.contains("messageType"));
        let decoded: ProtocolEnvelope<serde_json::Value> =
            serde_json::from_str(&json).expect("deserialize envelope");
        assert_eq!(decoded.message_type, "modules.list");
        assert!(decoded.error.is_none());
    }

    #[test]
    fn envelope_rejects_invalid_metadata() {
        let mut envelope = ProtocolEnvelope::new("modules.list", "scan-1", ());
        envelope.protocol_version = PROTOCOL_VERSION + 1;
        assert_eq!(
            envelope.validate().expect_err("version must fail").code,
            "protocolVersionUnsupported"
        );

        envelope.protocol_version = PROTOCOL_VERSION;
        envelope.message_type = "modules/list".to_string();
        assert_eq!(
            envelope.validate().expect_err("token must fail").code,
            "messageTypeInvalid"
        );
    }

    #[test]
    fn frame_decoder_accepts_one_line_and_rejects_unsafe_frames() {
        let frame =
            br#"{"protocolVersion":1,"messageType":"ping","requestId":"1","payload":{"ok":true}}
"#;
        let decoded: ProtocolEnvelope<serde_json::Value> = decode_line(frame).expect("valid frame");
        assert_eq!(decoded.message_type, "ping");

        assert_eq!(
            decode_line::<serde_json::Value>(b"{\xff}")
                .expect_err("invalid UTF-8")
                .code,
            "frameNotUtf8"
        );
        assert_eq!(
            decode_line::<serde_json::Value>(b"{}\nnext")
                .expect_err("multiple lines")
                .code,
            "frameInvalid"
        );
        assert_eq!(
            decode_line::<serde_json::Value>(&vec![b'x'; MAX_FRAME_BYTES + 1])
                .expect_err("oversized frame")
                .code,
            "frameTooLarge"
        );
        assert_eq!(
            decode_line::<serde_json::Value>(
                br#"{"protocolVersion":1,"messageType":"ping","requestId":"1","payload":{},"extra":true}"#,
            )
            .expect_err("unknown fields")
            .code,
            "frameInvalidJson"
        );
    }
}
