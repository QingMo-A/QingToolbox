use crate::model::{Error, Result};
use data_encoding::{BASE32, BASE32_NOPAD};
use hmac::{Hmac, Mac};
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::time::{SystemTime, UNIX_EPOCH};
use zeroize::{Zeroize, ZeroizeOnDrop, Zeroizing};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum Algorithm {
    Sha1,
    Sha256,
    Sha512,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Settings {
    pub algorithm: Algorithm,
    pub digits: u32,
    pub period_seconds: u32,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            algorithm: Algorithm::Sha1,
            digits: 6,
            period_seconds: 30,
        }
    }
}
impl Settings {
    pub fn validate(&self) -> Result<()> {
        if !matches!(self.digits, 6 | 8) || !(15..=300).contains(&self.period_seconds) {
            return Err(Error::new(
                "totpSettingsInvalid",
                "验证码位数仅支持 6 或 8 位，更新周期支持 15–300 秒。",
            ));
        }
        Ok(())
    }
}

#[derive(Clone, Serialize, Deserialize, Zeroize, ZeroizeOnDrop)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Config {
    pub secret: String,
    #[zeroize(skip)]
    pub settings: Settings,
}
#[derive(Clone, Copy, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ChangeAction {
    Keep,
    Set,
    Remove,
}
#[derive(Deserialize, Zeroize, ZeroizeOnDrop)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Change {
    #[zeroize(skip)]
    pub action: ChangeAction,
    #[serde(default)]
    pub secret: String,
    #[serde(default)]
    #[zeroize(skip)]
    pub settings: Settings,
}
impl Change {
    pub fn apply(&self, previous: Option<&Config>) -> Result<Option<Config>> {
        match self.action {
            ChangeAction::Set => Ok(Some(Config::parse(&self.secret, self.settings)?)),
            ChangeAction::Keep | ChangeAction::Remove if !self.secret.is_empty() => Err(
                Error::new("totpChangeInvalid", "密钥变更方式无效，请重新设置。"),
            ),
            ChangeAction::Remove => Ok(None),
            ChangeAction::Keep => {
                let mut config = previous
                    .cloned()
                    .ok_or_else(|| Error::new("totpSecretMissing", "请先填写身份验证器密钥。"))?;
                config.settings = self.settings;
                config.validate()?;
                Ok(Some(config))
            }
        }
    }
}

fn key_error() -> Error {
    Error::new(
        "totpSecretInvalid",
        "密钥格式无效：请填写有效的 Base32 密钥或 otpauth://totp/ 绑定链接。",
    )
}
fn decode(input: &str) -> Result<Zeroizing<Vec<u8>>> {
    if input.len() > 512 {
        return Err(key_error());
    }
    let normalized = Zeroizing::new(
        input
            .chars()
            .filter(|c| !c.is_ascii_whitespace() && *c != '-')
            .map(|c| c.to_ascii_uppercase())
            .collect::<String>(),
    );
    let bytes = Zeroizing::new(
        if normalized.contains('=') {
            BASE32.decode(normalized.as_bytes())
        } else {
            BASE32_NOPAD.decode(normalized.as_bytes())
        }
        .map_err(|_| key_error())?,
    );
    // Accept widely deployed 80-bit legacy seeds, without accepting tiny keys.
    if !(10..=128).contains(&bytes.len()) {
        return Err(key_error());
    }
    Ok(bytes)
}
impl Config {
    pub fn parse(input: &str, settings: Settings) -> Result<Self> {
        if input.len() > 4096 {
            return Err(key_error());
        }
        let input = input.trim();
        if input
            .get(..10)
            .is_some_and(|prefix| prefix.eq_ignore_ascii_case("otpauth://"))
        {
            let url = url::Url::parse(input).map_err(|_| key_error())?;
            let result = (|| {
                if url.scheme() != "otpauth"
                    || url.host_str() != Some("totp")
                    || !url.username().is_empty()
                    || url.password().is_some()
                    || url.port().is_some()
                    || url.fragment().is_some()
                {
                    return Err(key_error());
                }
                let mut seen = BTreeSet::new();
                let mut secret = Zeroizing::new(String::new());
                let mut settings = Settings::default();
                for (name, value) in url.query_pairs() {
                    let value = Zeroizing::new(value.into_owned());
                    if matches!(name.as_ref(), "secret" | "algorithm" | "digits" | "period")
                        && !seen.insert(name.to_string())
                    {
                        return Err(key_error());
                    }
                    match name.as_ref() {
                        "secret" => *secret = value.to_string(),
                        "algorithm" => {
                            settings.algorithm = match value.to_ascii_uppercase().as_str() {
                                "SHA1" => Algorithm::Sha1,
                                "SHA256" => Algorithm::Sha256,
                                "SHA512" => Algorithm::Sha512,
                                _ => return Err(key_error()),
                            }
                        }
                        "digits" => settings.digits = value.parse().map_err(|_| key_error())?,
                        "period" => {
                            settings.period_seconds = value.parse().map_err(|_| key_error())?
                        }
                        _ => (),
                    }
                }
                Self::from_key(&secret, settings)
            })();
            // Clear the URL's owned backing string as well as decoded parameters.
            let serialized: String = url.into();
            drop(Zeroizing::new(serialized));
            result
        } else {
            Self::from_key(input, settings)
        }
    }
    fn from_key(input: &str, settings: Settings) -> Result<Self> {
        settings.validate()?;
        let bytes = decode(input)?;
        Ok(Self {
            secret: BASE32_NOPAD.encode(&bytes),
            settings,
        })
    }
    pub fn validate(&self) -> Result<()> {
        self.settings.validate()?;
        decode(&self.secret)?;
        Ok(())
    }
    pub fn generate_at(&self, unix_ms: u64) -> Result<Code> {
        self.settings.validate()?;
        let key = decode(&self.secret)?;
        let period_ms = u64::from(self.settings.period_seconds) * 1000;
        let step = unix_ms / period_ms;
        let input = step.to_be_bytes();
        macro_rules! digest {
            ($hash:ty) => {{
                let mut mac = Hmac::<$hash>::new_from_slice(&key).map_err(|_| key_error())?;
                mac.update(&input);
                Zeroizing::new(mac.finalize().into_bytes().to_vec())
            }};
        }
        let digest = match self.settings.algorithm {
            Algorithm::Sha1 => digest!(sha1::Sha1),
            Algorithm::Sha256 => digest!(sha2::Sha256),
            Algorithm::Sha512 => digest!(sha2::Sha512),
        };
        let offset = usize::from(digest[digest.len() - 1] & 0x0f);
        let number = u32::from_be_bytes(
            digest[offset..offset + 4]
                .try_into()
                .map_err(|_| key_error())?,
        ) & 0x7fffffff;
        let valid_from_ms = step * period_ms;
        let valid_until_ms = valid_from_ms
            .checked_add(period_ms)
            .ok_or_else(clock_error)?;
        Ok(Code {
            code: format!(
                "{:0width$}",
                number % 10_u32.pow(self.settings.digits),
                width = self.settings.digits as usize
            ),
            generated_at_ms: unix_ms,
            valid_from_ms,
            valid_until_ms,
            period_seconds: self.settings.period_seconds,
        })
    }
    pub fn generate(&self) -> Result<Code> {
        let ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| clock_error())?
            .as_millis();
        self.generate_at(ms.try_into().map_err(|_| clock_error())?)
    }
}
fn clock_error() -> Error {
    Error::new("totpClockInvalid", "系统时间不可用，请检查电脑时间后重试。")
}
#[derive(Serialize, Zeroize, ZeroizeOnDrop)]
#[serde(rename_all = "camelCase")]
pub struct Code {
    pub code: String,
    pub generated_at_ms: u64,
    pub valid_from_ms: u64,
    pub valid_until_ms: u64,
    pub period_seconds: u32,
}

#[cfg(test)]
mod tests {
    use super::*;
    // RFC 6238 Appendix B public vectors, never a real account's seed.
    #[test]
    fn rfc_vectors_cover_every_algorithm_and_post_2038_times() {
        let times = [
            59,
            1111111109,
            1111111111,
            1234567890,
            2000000000,
            20000000000,
        ];
        let cases = [
            (
                Algorithm::Sha1,
                "12345678901234567890",
                [
                    "94287082", "07081804", "14050471", "89005924", "69279037", "65353130",
                ],
            ),
            (
                Algorithm::Sha256,
                "12345678901234567890123456789012",
                [
                    "46119246", "68084774", "67062674", "91819424", "90698825", "77737706",
                ],
            ),
            (
                Algorithm::Sha512,
                "1234567890123456789012345678901234567890123456789012345678901234",
                [
                    "90693936", "25091201", "99943326", "93441116", "38618901", "47863826",
                ],
            ),
        ];
        for (algorithm, key, expected) in cases {
            let config = Config::parse(
                &BASE32_NOPAD.encode(key.as_bytes()),
                Settings {
                    algorithm,
                    digits: 8,
                    period_seconds: 30,
                },
            )
            .unwrap();
            for (time, expected) in times.into_iter().zip(expected) {
                assert_eq!(config.generate_at(time * 1000).unwrap().code, expected);
            }
        }
    }
    #[test]
    fn boundaries_padding_and_leading_zeroes_are_correct() {
        let key = "gezd-gnbv gy3tqojq gezdgnbvgy3tqojq";
        let config = Config::parse(key, Settings::default()).unwrap();
        assert_eq!(config.generate_at(59_999).unwrap().code, "287082");
        let code = config.generate_at(60_000).unwrap();
        assert_eq!(code.valid_from_ms, 60_000);
        assert_eq!(code.valid_until_ms, 90_000);
        assert_eq!(
            config.generate_at(1_111_111_109_000).unwrap().code,
            "081804"
        );
    }
    #[test]
    fn uri_parameters_override_defaults_and_malformed_keys_never_echo() {
        let uri = "otpauth://totp/Example:test?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&algorithm=SHA256&digits=8&period=60";
        let config = Config::parse(uri, Settings::default()).unwrap();
        assert_eq!(
            config.settings,
            Settings {
                algorithm: Algorithm::Sha256,
                digits: 8,
                period_seconds: 60
            }
        );
        for bad in [
            "sensitive-invalid-seed!",
            "MZXW6",
            "otpauth://hotp/X?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",
            "otpauth://totp/X?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&secret=OTHER",
            "otpauth://totp/X?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&period=0",
        ] {
            let error = match Config::parse(bad, Settings::default()) {
                Err(error) => error,
                Ok(_) => panic!("invalid fixture accepted"),
            };
            assert!(!error.message.contains(bad));
        }
    }
}
