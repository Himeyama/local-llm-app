//! Token identity controls cache reuse. A checkpoint is valid only while the
//! shared prefix of the attention KV cache has not been overwritten by a reset.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Reuse {
    Current(usize),
    Checkpoint(usize),
    Reset,
}

pub fn choose(
    prompt: &[u32],
    current: &[u32],
    current_valid: bool,
    has_logits: bool,
    checkpoint: Option<&[u32]>,
) -> Reuse {
    if current_valid && prompt.starts_with(current) && (prompt.len() > current.len() || has_logits)
    {
        return Reuse::Current(current.len());
    }
    if let Some(tokens) = checkpoint.filter(|tokens| {
        !tokens.is_empty() && prompt.starts_with(tokens) && prompt.len() > tokens.len()
    }) {
        return Reuse::Checkpoint(tokens.len());
    }
    Reuse::Reset
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn reuses_valid_partial_prefill_and_exact_prompt_logits() {
        assert_eq!(
            choose(&[1, 2, 3], &[1, 2], true, false, None),
            Reuse::Current(2)
        );
        assert_eq!(
            choose(&[1, 2], &[1, 2], true, true, None),
            Reuse::Current(2)
        );
        assert_eq!(choose(&[1, 2], &[1, 2], true, false, None), Reuse::Reset);
    }
    #[test]
    fn canonicalized_assistant_history_restores_the_stable_checkpoint() {
        assert_eq!(
            choose(&[1, 2, 7, 8], &[1, 2, 3, 4], true, true, Some(&[1, 2])),
            Reuse::Checkpoint(2)
        );
        assert_eq!(
            choose(&[1, 2, 7, 8], &[1, 2, 7], false, false, Some(&[1, 2])),
            Reuse::Checkpoint(2)
        );
    }
    #[test]
    fn edited_messages_other_chats_and_shorter_prompts_do_not_reuse_state() {
        for prompt in [&[9, 2, 3][..], &[1][..], &[1, 9, 3][..]] {
            assert_eq!(
                choose(prompt, &[1, 2, 3], true, true, Some(&[1, 2])),
                Reuse::Reset
            );
        }
    }
}
