use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::BTreeSet;
use zeroize::{Zeroize, ZeroizeOnDrop};

#[derive(Debug, Clone, Serialize)]
pub struct Error {
    pub code: &'static str,
    pub message: String,
}
impl Error {
    pub fn new(code: &'static str, message: &str) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }
}
pub type Result<T> = std::result::Result<T, Error>;

pub const MAX_SECURITY_FIELDS: usize = 16;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SecurityFieldKind {
    Phone,
    Email,
    Custom,
}

#[derive(Clone, Serialize, Deserialize, Zeroize, ZeroizeOnDrop)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SecurityField {
    #[zeroize(skip)]
    pub kind: SecurityFieldKind,
    #[serde(default)]
    pub label: String,
    pub value: String,
}
impl SecurityField {
    pub fn label(&self) -> &str {
        match self.kind {
            SecurityFieldKind::Phone => "安全手机",
            SecurityFieldKind::Email => "安全邮箱",
            SecurityFieldKind::Custom => &self.label,
        }
    }
}

#[derive(Clone, Serialize, Deserialize, Zeroize, ZeroizeOnDrop)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Credential {
    pub id: String,
    pub account: String,
    pub password: String,
    pub note: String,
    #[serde(default)]
    pub security_fields: Vec<SecurityField>,
    #[serde(default)]
    pub totp: Option<crate::totp::Config>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Entry {
    pub id: String,
    pub name: String,
    pub link: String,
    pub remark: String,
    pub credentials: Vec<Credential>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Category {
    pub id: String,
    pub name: String,
    pub entries: Vec<Entry>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Vault {
    pub schema_version: u32,
    pub categories: Vec<Category>,
}
impl Default for Vault {
    fn default() -> Self {
        Self {
            schema_version: 3,
            categories: Vec::new(),
        }
    }
}

fn id() -> Result<String> {
    let mut bytes = [0_u8; 16];
    getrandom::fill(&mut bytes)
        .map_err(|_| Error::new("randomUnavailable", "无法生成安全的记录标识。"))?;
    Ok(bytes.iter().map(|v| format!("{v:02x}")).collect())
}
fn field(value: &str, max: usize, required: bool, multiline: bool) -> Result<()> {
    if value.len() > max
        || (required && value.trim().is_empty())
        || value
            .chars()
            .any(|c| c == '\0' || (!multiline && c.is_control()))
    {
        Err(Error::new(
            "invalidField",
            "字段为空、过长或包含不支持的控制字符。",
        ))
    } else {
        Ok(())
    }
}
fn identifier(value: &str) -> bool {
    value.len() == 32 && value.bytes().all(|v| v.is_ascii_hexdigit())
}

impl Vault {
    // Upgrade only in memory; reading an old vault never rewrites its files.
    // An older module will fail closed on the new schema rather than erase it.
    pub fn into_current_schema(mut self) -> Result<Self> {
        if matches!(self.schema_version, 1 | 2) {
            self.schema_version = 3;
        }
        self.validate()?;
        Ok(self)
    }
    pub fn snapshot(&self) -> Value {
        // Secrets never enter the regular list, refresh or search surface.
        json!({"categories":self.categories.iter().map(|c| json!({"id":c.id,"name":c.name,"entries":c.entries.iter().map(|e| json!({"id":e.id,"name":e.name,"link":e.link,"remark":e.remark,"credentials":e.credentials.iter().map(|a| json!({"id":a.id,"account":a.account,"note":a.note,"securityFields":a.security_fields,"totp":a.totp.as_ref().map(|config| config.settings)})).collect::<Vec<_>>()})).collect::<Vec<_>>()})).collect::<Vec<_>>()})
    }
    pub fn validate(&self) -> Result<()> {
        if self.schema_version != 3 || self.categories.len() > 64 {
            return Err(Error::new("vaultInvalid", "记事本格式或分类数量不受支持。"));
        }
        let mut ids = BTreeSet::new();
        let mut names = BTreeSet::new();
        let (mut entries, mut accounts) = (0, 0);
        for c in &self.categories {
            field(&c.name, 128, true, false)?;
            if !names.insert(c.name.to_lowercase()) {
                return Err(Error::new("duplicateCategory", "分类名称已存在。"));
            }
            if !identifier(&c.id) || !ids.insert(c.id.clone()) {
                return Err(Error::new("vaultInvalid", "记录标识无效或重复。"));
            }
            for e in &c.entries {
                entries += 1;
                if !identifier(&e.id) || !ids.insert(e.id.clone()) {
                    return Err(Error::new("vaultInvalid", "记录标识无效或重复。"));
                }
                field(&e.name, 256, true, false)?;
                field(&e.link, 2048, false, false)?;
                field(&e.remark, 4096, false, true)?;
                if !e.link.is_empty() && !crate::platform::valid_link(&e.link) {
                    return Err(Error::new(
                        "invalidLink",
                        "链接仅支持不含登录凭据的 HTTP / HTTPS 地址。",
                    ));
                }
                if e.credentials.is_empty() || e.credentials.len() > 64 {
                    return Err(Error::new(
                        "accountsInvalid",
                        "每个条目需要 1–64 组账号密码。",
                    ));
                }
                for a in &e.credentials {
                    accounts += 1;
                    if !identifier(&a.id) || !ids.insert(a.id.clone()) {
                        return Err(Error::new("vaultInvalid", "账号标识无效或重复。"));
                    }
                    field(&a.account, 512, true, false)?;
                    field(&a.password, 4096, false, false)?;
                    if a.password.is_empty() {
                        return Err(Error::new("passwordEmpty", "密码不能为空。"));
                    }
                    field(&a.note, 512, false, false)?;
                    if let Some(config) = &a.totp {
                        config.validate()?;
                    }
                    if a.security_fields.len() > MAX_SECURITY_FIELDS {
                        return Err(Error::new(
                            "securityFieldsTooMany",
                            "每组账号最多添加 16 条安全信息。",
                        ));
                    }
                    for info in &a.security_fields {
                        field(
                            &info.label,
                            128,
                            info.kind == SecurityFieldKind::Custom,
                            false,
                        )?;
                        field(&info.value, 1024, true, false)?;
                    }
                }
            }
        }
        if entries > 256
            || accounts > 500
            || serde_json::to_vec(&self.snapshot())
                .map_err(|_| Error::new("vaultInvalid", "无法检查记事本。"))?
                .len()
                > 768 * 1024
        {
            return Err(Error::new(
                "vaultTooLarge",
                "记事本已达到本版容量上限（256 个条目 / 500 组账号）。",
            ));
        }
        Ok(())
    }
    pub fn save_category(&mut self, category_id: Option<&str>, name: &str) -> Result<()> {
        let name = name.trim().to_string();
        if let Some(category_id) = category_id {
            self.categories
                .iter_mut()
                .find(|c| c.id == category_id)
                .ok_or_else(not_found)?
                .name = name;
        } else {
            self.categories.push(Category {
                id: id()?,
                name,
                entries: Vec::new(),
            });
        }
        self.validate()
    }
    pub fn delete_category(&mut self, category_id: &str) -> Result<()> {
        let index = self
            .categories
            .iter()
            .position(|c| c.id == category_id)
            .ok_or_else(not_found)?;
        self.categories.remove(index);
        Ok(())
    }
    pub fn entry(&self, entry_id: &str) -> Result<&Entry> {
        self.categories
            .iter()
            .flat_map(|c| &c.entries)
            .find(|e| e.id == entry_id)
            .ok_or_else(not_found)
    }
    pub fn credential(&self, entry_id: &str, credential_id: &str) -> Result<&Credential> {
        self.entry(entry_id)?
            .credentials
            .iter()
            .find(|a| a.id == credential_id)
            .ok_or_else(not_found)
    }
    pub fn entry_for_edit(&self, entry_id: &str) -> Result<Value> {
        let entry = self.entry(entry_id)?;
        // Editing may explicitly expose passwords, but never needs a saved
        // authenticator seed: keep/change/remove use metadata-only projections.
        Ok(
            json!({"id":entry.id,"name":entry.name,"link":entry.link,"remark":entry.remark,"credentials":entry.credentials.iter().map(|a| json!({"id":a.id,"account":a.account,"password":a.password,"note":a.note,"securityFields":a.security_fields,"totp":a.totp.as_ref().map(|config| config.settings)})).collect::<Vec<_>>() }),
        )
    }
    pub fn save_entry(&mut self, request: SaveEntry) -> Result<()> {
        let existing = if let Some(ref entry_id) = request.id {
            Some(self.entry(entry_id)?.clone())
        } else {
            None
        };
        let entry_id = match request.id.clone() {
            Some(value) => value,
            None => id()?,
        };
        let mut credentials = Vec::new();
        for a in &request.credentials {
            let credential_id = match &a.id {
                Some(credential_id)
                    if existing.as_ref().is_some_and(|e| {
                        e.credentials.iter().any(|old| &old.id == credential_id)
                    }) =>
                {
                    credential_id.clone()
                }
                Some(_) => return Err(Error::new("credentialMismatch", "账号标识不属于此条目。")),
                None => id()?,
            };
            let previous = existing
                .as_ref()
                .and_then(|entry| entry.credentials.iter().find(|old| old.id == credential_id));
            let totp = match &a.totp {
                Some(change) => change.apply(previous.and_then(|old| old.totp.as_ref()))?,
                None => previous.and_then(|old| old.totp.as_ref()).cloned(),
            };
            credentials.push(Credential {
                id: credential_id,
                account: a.account.trim().into(),
                password: a.password.clone(),
                note: a.note.trim().into(),
                totp,
                // Missing fields in a legacy edit mean 'leave them alone';
                // an explicit empty array is how the new editor clears them.
                security_fields: a
                    .security_fields
                    .clone()
                    .unwrap_or_else(|| {
                        existing
                            .as_ref()
                            .and_then(|e| {
                                e.credentials
                                    .iter()
                                    .find(|old| Some(&old.id) == a.id.as_ref())
                            })
                            .map(|old| old.security_fields.clone())
                            .unwrap_or_default()
                    })
                    .iter()
                    .map(|info| SecurityField {
                        kind: info.kind,
                        label: if info.kind == SecurityFieldKind::Custom {
                            info.label.trim().into()
                        } else {
                            String::new()
                        },
                        value: info.value.trim().into(),
                    })
                    .collect(),
            });
        }
        let entry = Entry {
            id: entry_id.clone(),
            name: request.name.trim().into(),
            link: request.link.trim().into(),
            remark: request.remark.trim().into(),
            credentials,
        };
        if !self.categories.iter().any(|c| c.id == request.category_id) {
            return Err(not_found());
        }
        if let Some(old) = self
            .categories
            .iter_mut()
            .find(|c| c.id == request.category_id)
            .and_then(|c| c.entries.iter_mut().find(|e| e.id == entry_id))
        {
            *old = entry;
        } else {
            for c in &mut self.categories {
                c.entries.retain(|e| e.id != entry_id);
            }
            self.categories
                .iter_mut()
                .find(|c| c.id == request.category_id)
                .ok_or_else(not_found)?
                .entries
                .push(entry);
        }
        self.validate()
    }
    pub fn delete_entry(&mut self, entry_id: &str) -> Result<()> {
        let c = self
            .categories
            .iter_mut()
            .find(|c| c.entries.iter().any(|e| e.id == entry_id))
            .ok_or_else(not_found)?;
        c.entries.retain(|e| e.id != entry_id);
        Ok(())
    }
    pub fn text(&self, category_id: Option<&str>) -> Result<String> {
        let mut out = String::from("密码记事本 · 明文导出\r\n注意：本文件包含真实密码，请妥善保管，不要发送给他人。\r\n\r\n");
        let categories: Vec<_> = self
            .categories
            .iter()
            .filter(|c| category_id.map_or(true, |id| c.id == id))
            .collect();
        if category_id.is_some() && categories.is_empty() {
            return Err(not_found());
        }
        for c in categories {
            out.push_str(&format!("【{}】\r\n", c.name));
            for e in &c.entries {
                out.push_str(&format!("游戏 / 网站：{}\r\n", e.name));
                if !e.link.is_empty() {
                    out.push_str(&format!("链接：{}\r\n", e.link));
                }
                if !e.remark.is_empty() {
                    out.push_str("备注：\r\n");
                    for line in e.remark.lines() {
                        out.push_str(&format!("  {line}\r\n"));
                    }
                }
                for (index, a) in e.credentials.iter().enumerate() {
                    out.push_str(&format!(
                        "  账号 {}：{}\r\n  密码：{}\r\n",
                        index + 1,
                        a.account,
                        a.password
                    ));
                    if !a.note.is_empty() {
                        out.push_str(&format!("  账号备注：{}\r\n", a.note));
                    }
                    for info in &a.security_fields {
                        out.push_str(&format!("  {}：{}\r\n", info.label(), info.value));
                    }
                }
                out.push_str("\r\n");
            }
        }
        Ok(out)
    }
}
pub fn not_found() -> Error {
    Error::new("notFound", "记录不存在，请刷新后重试。")
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SaveEntry {
    pub category_id: String,
    pub id: Option<String>,
    pub name: String,
    #[serde(default)]
    pub link: String,
    #[serde(default)]
    pub remark: String,
    pub credentials: Vec<CredentialInput>,
}
#[derive(Deserialize, Zeroize, ZeroizeOnDrop)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CredentialInput {
    pub id: Option<String>,
    pub account: String,
    pub password: String,
    #[serde(default)]
    pub note: String,
    #[serde(default)]
    pub security_fields: Option<Vec<SecurityField>>,
    #[serde(default)]
    pub totp: Option<crate::totp::Change>,
}

#[cfg(test)]
mod tests {
    use super::*;
    fn example() -> Vault {
        let mut v = Vault::default();
        v.save_category(None, "游戏").unwrap();
        let request = serde_json::from_value(json!({"categoryId":v.categories[0].id,"name":"示例游戏","link":"https://example.com","remark":"主区","credentials":[{"account":"one","password":" secret-one ","note":"主号"},{"account":"two","password":"secret-two"}]})).unwrap();
        v.save_entry(request).unwrap();
        v
    }
    #[test]
    fn lists_never_contain_passwords_and_export_preserves_them() {
        let v = example();
        let snapshot = v.snapshot().to_string();
        assert!(!snapshot.contains("secret-one") && !snapshot.contains("password"));
        assert_eq!(v.categories[0].entries[0].credentials.len(), 2);
        assert!(v.text(None).unwrap().contains("密码： secret-one "));
    }
    #[test]
    fn names_and_ownership_are_validated() {
        let mut v = example();
        assert!(v.save_category(None, " 游戏 ").is_err());
        let v = example();
        assert!(v
            .credential(&v.categories[0].entries[0].id, "unknown")
            .is_err());
    }
    #[test]
    fn removing_category_cascades_and_scoped_export_is_not_all_export() {
        let mut v = example();
        let id = v.categories[0].id.clone();
        v.save_category(None, "网站").unwrap();
        assert!(!v.text(Some(&id)).unwrap().contains("【网站】"));
        v.delete_category(&id).unwrap();
        assert_eq!(v.categories[0].entries.len(), 0);
        assert!(v.text(Some(&id)).is_err());
    }
    #[test]
    fn invalid_or_duplicate_identifiers_and_empty_passwords_fail() {
        let mut v = example();
        v.categories[0].entries[0].credentials[0].password.clear();
        assert!(v.validate().is_err());
        let mut v = example();
        v.categories[0].entries[0].id = v.categories[0].id.clone();
        assert!(v.validate().is_err());
    }

    fn with_security_fields() -> Vault {
        let mut v = example();
        let request = serde_json::from_value(json!({
            "categoryId":v.categories[0].id,
            "id":v.categories[0].entries[0].id,
            "name":"示例游戏",
            "credentials":[{
                "id":v.categories[0].entries[0].credentials[0].id,
                "account":"one","password":" secret-one ",
                "securityFields":[
                    {"kind":"phone","value":" +1 202 555 0100 "},
                    {"kind":"phone","value":"+44 7700 900123"},
                    {"kind":"email","value":"safe@example.com"},
                    {"kind":"custom","label":" 备用联系 ","value":" other@example.com "}
                ]
            }]
        }))
        .unwrap();
        v.save_entry(request).unwrap();
        v
    }
    #[test]
    fn multiple_security_fields_roundtrip_and_export_without_exposing_passwords() {
        let v = with_security_fields();
        let credential = &v.categories[0].entries[0].credentials[0];
        assert_eq!(credential.security_fields.len(), 4);
        assert_eq!(credential.security_fields[0].value, "+1 202 555 0100");
        assert_eq!(credential.security_fields[3].label, "备用联系");
        let snapshot = v.snapshot();
        assert_eq!(
            snapshot["categories"][0]["entries"][0]["credentials"][0]["securityFields"]
                .as_array()
                .unwrap()
                .len(),
            4
        );
        assert!(!snapshot.to_string().contains("secret-one"));
        let export = v.text(None).unwrap();
        assert!(export.contains("安全手机：+44 7700 900123"));
        assert!(export.contains("安全邮箱：safe@example.com"));
        assert!(export.contains("备用联系：other@example.com"));
        let reloaded: Vault = serde_json::from_slice(&serde_json::to_vec(&v).unwrap()).unwrap();
        assert_eq!(reloaded.snapshot(), snapshot);
    }
    #[test]
    fn legacy_accounts_gain_empty_fields_and_unknown_versions_fail_closed() {
        let mut old = serde_json::to_value(example()).unwrap();
        old["schemaVersion"] = json!(1);
        for account in old["categories"][0]["entries"][0]["credentials"]
            .as_array_mut()
            .unwrap()
        {
            account.as_object_mut().unwrap().remove("securityFields");
            account.as_object_mut().unwrap().remove("totp");
        }
        let upgraded = serde_json::from_value::<Vault>(old)
            .unwrap()
            .into_current_schema()
            .unwrap();
        assert_eq!(upgraded.schema_version, 3);
        assert!(upgraded.categories[0].entries[0].credentials[0]
            .security_fields
            .is_empty());
        assert_eq!(
            upgraded.categories[0].entries[0].credentials[0].password,
            " secret-one "
        );
        let mut unsupported = upgraded;
        unsupported.schema_version = 4;
        assert!(unsupported.into_current_schema().is_err());
    }
    #[test]
    fn missing_fields_preserve_existing_values_but_empty_array_removes_them() {
        let mut v = with_security_fields();
        let mut request = json!({"categoryId":v.categories[0].id,"id":v.categories[0].entries[0].id,"name":"改名","credentials":[{"id":v.categories[0].entries[0].credentials[0].id,"account":"one","password":"same-password"}]});
        v.save_entry(serde_json::from_value(request.clone()).unwrap())
            .unwrap();
        assert_eq!(
            v.categories[0].entries[0].credentials[0]
                .security_fields
                .len(),
            4
        );
        request["credentials"][0]["securityFields"] = json!([]);
        v.save_entry(serde_json::from_value(request).unwrap())
            .unwrap();
        assert!(v.categories[0].entries[0].credentials[0]
            .security_fields
            .is_empty());
    }
    #[test]
    fn security_field_limits_controls_and_custom_names_are_validated() {
        let mut v = with_security_fields();
        let credential = &mut v.categories[0].entries[0].credentials[0];
        credential.security_fields = (0..=MAX_SECURITY_FIELDS)
            .map(|_| SecurityField {
                kind: SecurityFieldKind::Email,
                label: String::new(),
                value: "safe@example.com".into(),
            })
            .collect();
        assert_eq!(v.validate().unwrap_err().code, "securityFieldsTooMany");
        for (kind, label, value) in [
            (SecurityFieldKind::Custom, "", "value"),
            (SecurityFieldKind::Phone, "", "\nprivate-value"),
            (SecurityFieldKind::Email, "", " "),
        ] {
            let mut v = example();
            v.categories[0].entries[0].credentials[0].security_fields = vec![SecurityField {
                kind,
                label: label.into(),
                value: value.into(),
            }];
            let error = v.validate().unwrap_err();
            assert!(!error.message.contains("private-value"));
        }
        assert!(
            serde_json::from_value::<SecurityField>(json!({"kind":"unknown","value":"value"}))
                .is_err()
        );
        let mut field = SecurityField {
            kind: SecurityFieldKind::Custom,
            label: "contact".into(),
            value: "sensitive-value".into(),
        };
        field.zeroize();
        assert!(field.label.is_empty() && field.value.is_empty());
    }
    #[test]
    fn totp_seed_never_enters_lists_edit_responses_or_plaintext_exports() {
        let mut v = example();
        let secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        let entry_id = v.categories[0].entries[0].id.clone();
        let credential_id = v.categories[0].entries[0].credentials[0].id.clone();
        let mut request = json!({"categoryId":v.categories[0].id,"id":entry_id,"name":"TOTP fixture","credentials":[{"id":credential_id,"account":"one","password":"synthetic-password","totp":{"action":"set","secret":secret}}]});
        v.save_entry(serde_json::from_value(request.clone()).unwrap())
            .unwrap();
        assert_eq!(
            v.snapshot()["categories"][0]["entries"][0]["credentials"][0]["totp"]["periodSeconds"],
            30
        );
        for projected in [
            v.snapshot().to_string(),
            v.entry_for_edit(&entry_id).unwrap().to_string(),
            v.text(None).unwrap(),
        ] {
            assert!(!projected.contains(secret));
            assert!(!projected.contains("\"secret\""));
        }
        request["credentials"][0]
            .as_object_mut()
            .unwrap()
            .remove("totp");
        v.save_entry(serde_json::from_value(request.clone()).unwrap())
            .unwrap();
        assert!(v
            .credential(&entry_id, &credential_id)
            .unwrap()
            .totp
            .is_some());
        request["credentials"][0]["totp"] = json!({"action":"keep","settings":{"algorithm":"SHA256","digits":8,"periodSeconds":60}});
        v.save_entry(serde_json::from_value(request.clone()).unwrap())
            .unwrap();
        let config = v
            .credential(&entry_id, &credential_id)
            .unwrap()
            .totp
            .as_ref()
            .unwrap();
        assert_eq!(config.secret, secret);
        assert_eq!(config.settings.period_seconds, 60);
        request["credentials"][0]["totp"] = json!({"action":"remove"});
        v.save_entry(serde_json::from_value(request).unwrap())
            .unwrap();
        assert!(v
            .credential(&entry_id, &credential_id)
            .unwrap()
            .totp
            .is_none());
    }
}
