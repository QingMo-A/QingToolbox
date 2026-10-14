use crate::{
    model::{Error, Result, SaveEntry, Vault},
    platform,
    store::Store,
};
use serde_json::{json, Value};
use std::path::PathBuf;
use zeroize::Zeroizing;

pub struct App {
    vault: Vault,
    store: Option<Store>,
    load_error: Option<Error>,
}
fn text<'a>(value: &'a Value, key: &str) -> Result<&'a str> {
    value
        .get(key)
        .and_then(Value::as_str)
        .ok_or_else(|| Error::new("invalidPayload", "请求字段不完整。"))
}
fn optional_text<'a>(value: &'a Value, key: &str) -> Result<Option<&'a str>> {
    match value.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(v)) if !v.is_empty() => Ok(Some(v)),
        _ => Err(Error::new("invalidPayload", "请求的记录范围无效。")),
    }
}
impl App {
    pub fn new(root: Option<PathBuf>) -> Self {
        let store = root.map(|root| Store { root });
        let result = store
            .as_ref()
            .ok_or_else(|| Error::new("dataDirectoryMissing", "宿主未提供模块的数据目录。"))
            .and_then(Store::load);
        match result {
            Ok(vault) => Self {
                vault,
                store,
                load_error: None,
            },
            Err(error) => Self {
                vault: Vault::default(),
                store,
                load_error: Some(error),
            },
        }
    }
    pub fn snapshot(&self) -> Value {
        let mut value = self.vault.snapshot();
        value["loadError"] = self
            .load_error
            .as_ref()
            .map(|e| json!({"code":e.code,"message":e.message}))
            .unwrap_or(Value::Null);
        value["storage"] = json!("windows-user-dpapi");
        value
    }
    fn ready(&self) -> Result<()> {
        self.load_error.as_ref().map_or(Ok(()), |e| Err(e.clone()))
    }
    fn edit(&mut self, operation: impl FnOnce(&mut Vault) -> Result<()>) -> Result<Value> {
        self.ready()?;
        let mut next = self.vault.clone();
        operation(&mut next)?;
        next.validate()?;
        self.store
            .as_ref()
            .ok_or_else(|| Error::new("dataDirectoryMissing", "没有数据目录。"))?
            .save(&next)?;
        self.vault = next;
        Ok(self.snapshot())
    }
    pub fn invoke(&mut self, method: &str, payload: &Value) -> Result<Value> {
        if method == "getState" {
            return Ok(self.snapshot());
        }
        self.ready()?;
        match method {
            "saveCategory" => {
                let name = text(payload, "name")?;
                let id = optional_text(payload, "id")?;
                self.edit(|v| v.save_category(id, name))
            }
            "deleteCategory" => {
                let id = text(payload, "id")?;
                self.edit(|v| v.delete_category(id))
            }
            "saveEntry" => {
                let encoded = bytes(payload)?;
                let request: SaveEntry = serde_json::from_slice(&encoded)
                    .map_err(|_| Error::new("invalidPayload", "条目字段不完整或类型无效。"))?;
                self.edit(|v| v.save_entry(request))
            }
            "deleteEntry" => {
                let id = text(payload, "id")?;
                self.edit(|v| v.delete_entry(id))
            }
            "getEntryForEdit" => self.vault.entry_for_edit(text(payload, "id")?),
            "getTotp" | "copyTotp" => {
                let config = self
                    .vault
                    .credential(text(payload, "entryId")?, text(payload, "credentialId")?)?
                    .totp
                    .as_ref()
                    .ok_or_else(|| {
                        Error::new("totpNotConfigured", "此账号尚未设置身份验证器密钥。")
                    })?;
                let code = config.generate()?;
                if method == "copyTotp" {
                    platform::copy(&code.code)?;
                }
                let mut value = serde_json::to_value(&code)
                    .map_err(|_| Error::new("readFailed", "无法生成验证码。"))?;
                if method == "copyTotp" {
                    value["copied"] = json!(true);
                }
                Ok(value)
            }
            "getPassword" => Ok(
                json!({"password":self.vault.credential(text(payload,"entryId")?,text(payload,"credentialId")?)?.password}),
            ),
            "copyCredential" => {
                let credential = self
                    .vault
                    .credential(text(payload, "entryId")?, text(payload, "credentialId")?)?;
                let value = match text(payload, "field")? {
                    "account" => &credential.account,
                    "password" => &credential.password,
                    "securityField" => {
                        let index = payload
                            .get("securityFieldIndex")
                            .and_then(Value::as_u64)
                            .and_then(|index| usize::try_from(index).ok())
                            .ok_or_else(|| Error::new("invalidField", "安全信息序号无效。"))?;
                        &credential
                            .security_fields
                            .get(index)
                            .ok_or_else(|| {
                                Error::new("invalidField", "安全信息不存在，请刷新后重试。")
                            })?
                            .value
                    }
                    _ => return Err(Error::new("invalidField", "只能复制账号、密码或安全信息。")),
                };
                platform::copy(value)?;
                Ok(json!({"copied":true}))
            }
            "copyLink" => {
                let entry = self.vault.entry(text(payload, "id")?)?;
                if entry.link.is_empty() {
                    return Err(Error::new("linkEmpty", "此条目没有链接。"));
                }
                platform::copy(&entry.link)?;
                Ok(json!({"copied":true}))
            }
            "exportTxt" => {
                if payload.get("includePasswords").and_then(Value::as_bool) != Some(true) {
                    return Err(Error::new(
                        "exportNotConfirmed",
                        "导出包含明文密码，需要先确认风险。",
                    ));
                }
                let path = PathBuf::from(text(payload, "path")?);
                let category_id = optional_text(payload, "categoryId")?;
                self.store
                    .as_ref()
                    .ok_or_else(|| Error::new("dataDirectoryMissing", "没有数据目录。"))?
                    .export(&self.vault, &path, category_id)?;
                Ok(json!({"exported":true}))
            }
            _ => Err(Error::new("unknownMethod", "未知的密码记事本操作。")),
        }
    }
}

/// Do not leave credential strings in the parsed protocol frame after dispatch.
pub fn wipe(value: &mut Value) {
    use zeroize::Zeroize;
    match value {
        Value::String(v) => v.zeroize(),
        Value::Array(v) => v.iter_mut().for_each(wipe),
        Value::Object(v) => v.values_mut().for_each(wipe),
        _ => (),
    }
}
pub fn bytes(value: &Value) -> Result<Zeroizing<Vec<u8>>> {
    serde_json::to_vec(value)
        .map(Zeroizing::new)
        .map_err(|_| Error::new("serializeFailed", "无法生成模块响应。"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn unreadable_store_does_not_allow_mutations_or_fake_empty_success() {
        let mut app = App::new(None);
        assert_eq!(app.snapshot()["loadError"]["code"], "dataDirectoryMissing");
        assert!(app
            .invoke("saveCategory", &json!({"name":"新分类"}))
            .is_err());
        assert!(app.vault.categories.is_empty());
    }
    #[test]
    #[cfg(windows)]
    fn failed_save_does_not_change_the_resident_document() {
        let root =
            std::env::temp_dir().join(format!("qing-passwordnotes-fail-{}", std::process::id()));
        let mut app = App::new(Some(root.clone()));
        app.invoke("saveCategory", &json!({"name":"分类"})).unwrap();
        let before = app.snapshot();
        assert!(app.invoke("saveCategory", &json!({"name":"分类"})).is_err());
        assert_eq!(app.snapshot(), before);
        assert!(app
            .invoke("exportTxt", &json!({"path":root.join("no.txt")}))
            .is_err());
        assert!(!root.join("no.txt").exists());
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn wipe_clears_nested_strings() {
        let mut value = json!({"credentials":[{"password":"synthetic"}]});
        wipe(&mut value);
        assert_eq!(value["credentials"][0]["password"], "");
    }
    #[test]
    #[cfg(windows)]
    fn invalid_security_copy_requests_are_rejected_before_the_clipboard_is_touched() {
        let root =
            std::env::temp_dir().join(format!("qing-passwordnotes-copy-{}", std::process::id()));
        let mut app = App::new(Some(root.clone()));
        app.invoke("saveCategory", &json!({"name":"分类"})).unwrap();
        app.invoke("saveEntry", &json!({"categoryId":app.vault.categories[0].id,"name":"示例","credentials":[{"account":"synthetic","password":"synthetic-password","securityFields":[{"kind":"email","value":"safe@example.com"}]}]})).unwrap();
        for index in [json!(-1), json!(100), json!(null), json!(0.5), json!("0")] {
            let payload = json!({"entryId":app.vault.categories[0].entries[0].id,"credentialId":app.vault.categories[0].entries[0].credentials[0].id,"field":"securityField","securityFieldIndex":index});
            assert_eq!(
                app.invoke("copyCredential", &payload).unwrap_err().code,
                "invalidField"
            );
        }
        std::fs::remove_dir_all(root).unwrap();
    }
}
