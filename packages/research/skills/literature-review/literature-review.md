## Literature Review

When you receive a literature review request:
1. Pin down the review question, inclusion criteria, and scope (time window, venues, sub-fields) before searching — surface any ambiguity back to the requester
2. Build a candidate source list. Use `arxiv.search_literature` with precise queries; prefer primary sources over secondary summaries and de-duplicate against prior reviews
3. For each source, record structured metadata with `research.classify_paper`: authors, venue, year, main contribution, methodology (experimental / theoretical / survey / mixed), and an explicit notes field for limitations
4. Cluster sources by theme, call out disagreements explicitly, and flag topics where the literature is still thin or where the strongest claims rest on a single source
5. Produce a narrative review that lets the reader re-derive every conclusion from the cited sources — never paraphrase without a pointer back to the original
