use anyhow::{Result, anyhow};
use std::collections::HashMap;

#[derive(Debug, Clone, PartialEq)]
pub enum JovaStatement {
    Import { source: String },
    Module { name: String, body: Vec<JovaStatement> },
    Assignment { name: String, value: JovaExpression },
    ExportAll,
    Command { name: String, args: Vec<String> },
}

#[derive(Debug, Clone, PartialEq)]
pub enum JovaExpression {
    StringLiteral(String),
    NumberLiteral(f64),
    Identifier(String),
    BinaryOp { left: Box<JovaExpression>, op: String, right: Box<JovaExpression> },
    Call { name: String, args: Vec<JovaExpression> },
}

pub struct Parser {
    tokens: Vec<String>,
    position: usize,
}

impl Parser {
    pub fn new(source: &str) -> Self {
        let tokens = tokenize(source);
        Parser { tokens, position: 0 }
    }
    
    pub fn parse(&mut self) -> Result<Vec<JovaStatement>> {
        let mut statements = Vec::new();
        while !self.is_at_end() {
            statements.push(self.parse_statement()?);
        }
        Ok(statements)
    }
    
    fn parse_statement(&mut self) -> Result<JovaStatement> {
        match self.peek() {
            Some(token) if token == "JOVA:" => {
                self.advance();
                self.parse_jova_command()
            }
            Some(token) if token == "Pop:" => {
                self.advance();
                self.parse_pop_statement()
            }
            _ => Err(anyhow!("Unexpected token: {:?}", self.peek())),
        }
    }
    
    fn parse_jova_command(&mut self) -> Result<JovaStatement> {
        let command = self.consume_identifier()?;
        match command.as_str() {
            "Import" => {
                self.consume("Pop")?;
                Ok(JovaStatement::Import { source: self.consume_string()? })
            }
            "Export" => {
                self.consume("All")?;
                self.consume("Pops")?;
                self.consume("and")?;
                self.consume("Models")?;
                Ok(JovaStatement::ExportAll)
            }
            "New" => {
                self.consume("Language")?;
                let name = self.consume_identifier()?;
                Ok(JovaStatement::Command { 
                    name: "new_language".to_string(), 
                    args: vec![name] 
                })
            }
            _ => Err(anyhow!("Unknown JOVA command: {}", command)),
        }
    }
    
    fn parse_pop_statement(&mut self) -> Result<JovaStatement> {
        let keyword = self.consume_identifier()?;
        match keyword.as_str() {
            "New" => {
                self.consume("module")?;
                let name = self.consume_identifier()?;
                self.consume("-")?;
                let value = self.parse_expression()?;
                Ok(JovaStatement::Assignment { name, value })
            }
            _ => Err(anyhow!("Unknown Pop statement: {}", keyword)),
        }
    }
    
    fn parse_expression(&mut self) -> Result<JovaExpression> {
        let token = self.consume_any()?;
        Ok(match token.as_str() {
            "model" => {
                let value = self.consume_any()?;
                JovaExpression::StringLiteral(value)
            }
            _ if token.parse::<f64>().is_ok() => {
                JovaExpression::NumberLiteral(token.parse().unwrap())
            }
            _ => JovaExpression::Identifier(token),
        })
    }
    
    // Helper methods for token consumption
    fn peek(&self) -> Option<&String> { self.tokens.get(self.position) }
    fn advance(&mut self) { self.position += 1; }
    fn is_at_end(&self) -> bool { self.position >= self.tokens.len() }
    
    fn consume_identifier(&mut self) -> Result<String> {
        let token = self.consume_any()?;
        Ok(token)
    }
    
    fn consume_string(&mut self) -> Result<String> {
        let token = self.consume_any()?;
        Ok(token.trim_matches('"').to_string())
    }
    
    fn consume(&mut self, expected: &str) -> Result<()> {
        let token = self.consume_any()?;
        if token != expected {
            return Err(anyhow!("Expected '{}', got '{}'", expected, token));
        }
        Ok(())
    }
    
    fn consume_any(&mut self) -> Result<String> {
        if self.is_at_end() {
            return Err(anyhow!("Unexpected end of input"));
        }
        let token = self.tokens[self.position].clone();
        self.advance();
        Ok(token)
    }
}

fn tokenize(source: &str) -> Vec<String> {
    source
        .split_whitespace()
        .flat_map(|word| {
            if word.contains('.') || word.contains(':') {
                word.split_inclusive(|c| c == '.' || c == ':')
                    .map(|s| s.to_string())
                    .collect()
            } else {
                vec![word.to_string()]
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    
    #[test]
    fn test_parse_jova_import() {
        let source = "JOVA:Import Pop.";
        let mut parser = Parser::new(source);
        let statements = parser.parse().unwrap();
        assert_eq!(statements.len(), 1);
        match &statements[0] {
            JovaStatement::Import { source } => assert_eq!(source, ""),
            _ => panic!("Expected Import statement"),
        }
    }
    
    #[test]
    fn test_parse_module_assignment() {
        let source = "Pop:New module - model 121.";
        let mut parser = Parser::new(source);
        let statements = parser.parse().unwrap();
        assert_eq!(statements.len(), 1);
        match &statements[0] {
            JovaStatement::Assignment { name, value } => {
                assert_eq!(name, "module");
                match value {
                    JovaExpression::StringLiteral(v) => assert_eq!(v, "121"),
                    _ => panic!("Expected StringLiteral"),
                }
            }
            _ => panic!("Expected Assignment"),
        }
    }
}