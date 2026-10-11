//! Small, bounded Markdown dialect. Only author-written syntax is interpreted:
//! provider values and placeholder fallbacks are always literal text.
use crate::text_template::Values;
use serde::Serialize;

pub const MAX_OUTPUT: usize = 256;
const MAX_LINES: usize = 16;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
pub struct Style {
    pub bold: bool,
    pub italic: bool,
    pub strike: bool,
    pub code: bool,
}
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum Run {
    Text { text: String, style: Style },
    Icon { name: String, style: Style },
}
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct Line {
    pub left: Vec<Run>,
    pub right: Vec<Run>,
}
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct Document {
    pub lines: Vec<Line>,
}
impl Document {
    pub fn is_empty(&self) -> bool {
        self.lines
            .iter()
            .all(|line| line.left.is_empty() && line.right.is_empty())
    }
    pub fn plain(&self) -> String {
        self.lines
            .iter()
            .map(|line| {
                let text = |runs: &[Run]| {
                    runs.iter()
                        .map(|run| match run {
                            Run::Text { text, .. } => text.as_str(),
                            Run::Icon { .. } => "",
                        })
                        .collect::<String>()
                };
                let left = text(&line.left);
                let right = text(&line.right);
                format!(
                    "{left}{}{right}",
                    if !left.is_empty() && !right.is_empty() {
                        " "
                    } else {
                        ""
                    }
                )
            })
            .collect::<Vec<_>>()
            .join("\n")
    }
}
enum Token {
    Run(Run),
    Space,
    Newline,
}

fn literal(out: &mut Vec<Token>, text: &str, style: Style, budget: &mut usize) {
    for ch in text.chars().take(MAX_OUTPUT) {
        if *budget == 0 {
            break;
        }
        *budget -= 1;
        if ch == '\n' {
            out.push(Token::Newline);
            continue;
        }
        if ch.is_control() {
            continue;
        }
        if let Some(Token::Run(Run::Text {
            text,
            style: previous,
        })) = out.last_mut()
        {
            if *previous == style {
                text.push(ch);
                continue;
            }
        }
        out.push(Token::Run(Run::Text {
            text: ch.to_string(),
            style,
        }));
    }
}

/// Find an unescaped matching delimiter without reading syntax inside a token.
fn closing(text: &str, marker: &str) -> Option<usize> {
    let mut offset = 0;
    while offset < text.len() {
        let rest = &text[offset..];
        if rest.starts_with('\\') {
            offset += 1;
            if let Some(ch) = text[offset..].chars().next() {
                offset += ch.len_utf8();
            }
        } else if rest.starts_with('{') && rest.contains('}') {
            offset += rest.find('}').unwrap() + 1;
        } else if rest.starts_with(marker) {
            return Some(offset);
        } else {
            offset += rest.chars().next().unwrap().len_utf8();
        }
    }
    None
}
fn parse(
    text: &str,
    values: &Values,
    fallback: &str,
    style: Style,
    depth: usize,
    out: &mut Vec<Token>,
    budget: &mut usize,
) {
    let mut rest = text;
    while !rest.is_empty() && *budget > 0 {
        if let Some(tail) = rest.strip_prefix('\\') {
            if let Some(ch) = tail.chars().next() {
                if "\\*_~`{}".contains(ch) {
                    literal(out, &ch.to_string(), style, budget);
                    rest = &tail[ch.len_utf8()..];
                    continue;
                }
            }
        }
        if rest.starts_with("{{") || rest.starts_with("}}") {
            literal(out, &rest[..1], style, budget);
            rest = &rest[2..];
            continue;
        }
        if rest.starts_with('{') {
            if let Some(end) = rest.find('}') {
                let body = &rest[1..end];
                let (key, missing) = body.split_once('|').unwrap_or((body, fallback));
                let key = key.trim();
                if body == "space" {
                    out.push(Token::Space);
                    rest = &rest[end + 1..];
                    continue;
                }
                if matches!(body, "icon.clock" | "icon.status") {
                    out.push(Token::Run(Run::Icon {
                        name: body[5..].into(),
                        style,
                    }));
                    *budget -= 1;
                    rest = &rest[end + 1..];
                    continue;
                }
                if let Some(value) = values.get(key) {
                    literal(out, value.as_deref().unwrap_or(missing), style, budget);
                    rest = &rest[end + 1..];
                    continue;
                }
                // Unknown tokens are literal as a whole, not nested markup.
                literal(out, &rest[..end + 1], style, budget);
                rest = &rest[end + 1..];
                continue;
            }
        }
        let mut matched = false;
        if depth < 8 {
            for marker in ["***", "___", "**", "__", "~~", "*", "_", "`"] {
                if !rest.starts_with(marker) {
                    continue;
                }
                let tail = &rest[marker.len()..];
                if let Some(end) = closing(tail, marker).filter(|end| *end > 0) {
                    let mut nested = style;
                    match marker {
                        "***" | "___" => {
                            nested.bold = true;
                            nested.italic = true;
                        }
                        "**" | "__" => nested.bold = true,
                        "~~" => nested.strike = true,
                        "*" | "_" => nested.italic = true,
                        _ => nested.code = true,
                    }
                    if nested.code {
                        literal(out, &tail[..end], nested, budget);
                    } else {
                        parse(
                            &tail[..end],
                            values,
                            fallback,
                            nested,
                            depth + 1,
                            out,
                            budget,
                        );
                    }
                    rest = &tail[end + marker.len()..];
                    matched = true;
                    break;
                }
            }
        }
        if matched {
            continue;
        }
        let ch = rest.chars().next().unwrap();
        literal(out, &ch.to_string(), style, budget);
        rest = &rest[ch.len_utf8()..];
    }
}

pub fn render(template: &str, values: &Values, fallback: &str) -> Document {
    let mut tokens = Vec::new();
    // Parse the complete bounded source before limiting display output: even
    // after a long left segment, the final spacer must still win. Each value
    // is bounded separately, then the right side gets display priority.
    let source: String = template
        .chars()
        .take(crate::settings::MAX_CUSTOM_TEXT_CHARS)
        .collect();
    let mut budget = 65536;
    parse(
        &source,
        values,
        fallback,
        Style::default(),
        0,
        &mut tokens,
        &mut budget,
    );
    let mut document = Document::default();
    let mut row = Vec::new();
    let flush = |row: &mut Vec<Token>, document: &mut Document| {
        if document.lines.len() >= MAX_LINES {
            row.clear();
            return;
        }
        let last = row.iter().rposition(|token| matches!(token, Token::Space));
        let mut line = Line::default();
        for (i, token) in row.drain(..).enumerate() {
            if let Token::Run(run) = token {
                if last.is_some_and(|last| i > last) {
                    line.right.push(run);
                } else {
                    line.left.push(run);
                }
            }
        }
        document.lines.push(line);
    };
    for token in tokens {
        if matches!(token, Token::Newline) {
            flush(&mut row, &mut document);
        } else {
            row.push(token);
        }
    }
    flush(&mut row, &mut document);
    let mut remaining = MAX_OUTPUT;
    for line in &mut document.lines {
        limit_runs(&mut line.right, &mut remaining);
        limit_runs(&mut line.left, &mut remaining);
    }
    document
}

fn limit_runs(runs: &mut Vec<Run>, remaining: &mut usize) {
    runs.retain_mut(|run| {
        if *remaining == 0 {
            return false;
        }
        match run {
            Run::Icon { .. } => *remaining -= 1,
            Run::Text { text, .. } => {
                *text = text.chars().take(*remaining).collect();
                *remaining -= text.chars().count();
                if text.is_empty() {
                    return false;
                }
            }
        }
        true
    });
}

/// Old versions rendered punctuation literally; preserve it during migration.
pub fn escape_legacy(text: &str) -> String {
    let mut out = String::new();
    let mut rest = text;
    while !rest.is_empty() {
        if rest.starts_with('{') {
            if let Some(end) = rest.find('}') {
                let body = &rest[1..end];
                if matches!(body, "space" | "icon.clock" | "icon.status") {
                    out.push('\\');
                }
                out.push_str(&rest[..end + 1]);
                rest = &rest[end + 1..];
                continue;
            }
        }
        let ch = rest.chars().next().unwrap();
        if "\\*_~`".contains(ch) {
            out.push('\\');
        }
        out.push(ch);
        rest = &rest[ch.len_utf8()..];
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn shared_vectors() {
        let cases: serde_json::Value =
            serde_json::from_str(include_str!("../contracts/rich-text-v1.json")).unwrap();
        for case in cases.as_array().unwrap() {
            let values: Values = serde_json::from_value(case["values"].clone()).unwrap();
            assert_eq!(
                serde_json::to_value(render(case["input"].as_str().unwrap(), &values, "暂无数据"))
                    .unwrap(),
                case["output"],
                "{}",
                case["name"]
            );
        }
    }
    #[test]
    fn bounded_and_unknown_markup_safe() {
        assert_eq!(
            render(&"字".repeat(4000), &Values::new(), "")
                .plain()
                .chars()
                .count(),
            MAX_OUTPUT
        );
        assert_eq!(
            render("<script>bad</script>", &Values::new(), "").plain(),
            "<script>bad</script>"
        );
        assert_eq!(render("*unclosed", &Values::new(), "").plain(), "*unclosed");
        assert_eq!(
            render(&escape_legacy("**原样**"), &Values::new(), "").plain(),
            "**原样**"
        );
        let document = render(
            &format!("A{{space}}{}{{space}}R", "字".repeat(400)),
            &Values::new(),
            "",
        );
        assert_eq!(
            document.lines[0].right,
            vec![Run::Text {
                text: "R".into(),
                style: Style::default()
            }]
        );
        assert!(
            document.lines[0].left.len() > 1,
            "earlier spacer must be ignored even when output is truncated"
        );
        assert_eq!(
            document.lines[0]
                .left
                .iter()
                .map(|run| match run {
                    Run::Text { text, .. } => text.chars().count(),
                    _ => 1,
                })
                .sum::<usize>(),
            MAX_OUTPUT - 1
        );
    }
}
