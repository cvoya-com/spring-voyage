## PR Review Cycle

When a pull request is ready for review:

1. **Pick the reviewer** best placed to judge the change — usually the tech lead for architectural alignment, plus whoever owns the surface it touches.
2. **Review against what matters.** Check correctness, test coverage for the change, and adherence to the project's conventions and scope discipline — the PR should ship what it declared, with no unrelated changes folded in.
3. **Route feedback.** When changes are requested, send specific, actionable notes back to the author and track the revision until it's addressed.
4. **Re-review after changes** rather than rubber-stamping: confirm the requested changes actually landed and the build / lint / test gates are still green.
5. **Record the decision** on the pull request — approve, request changes, or comment — with a short summary of the key points, so the history explains why it merged.
