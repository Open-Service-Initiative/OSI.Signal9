# Decisions and maintainer review

Who settles a question that comes up while building. Applies to agents and people alike: a coordinator answering a worker, an implementer stuck on a ticket, a reviewer.

## Settled: answer it and cite the source

If the answer is in one of these, it's already decided. Answer from it and name where it came from.

- The spec issue and the ticket.
- `docs/adr/`, `GLOSSARY.md` and the API conventions in `AGENTS.md`.
- Anything findable in the code, the docs or by running something. Finding facts is never a reason to stop.

## Requires human maintainer review

A choice requires human maintainer review when it would:

- change a spec, an ADR or a glossary term;
- make an architecture or high-level design choice the sources above don't already make;
- close a one-way door: public API shape, database schema, security or authorization behaviour, a new dependency or licence;
- go beyond the ticket's scope.

That review happens on the pull request to `dev`, not in conversation. Make the choice the sources point to most strongly, keep it easy to change, and keep building. Then list it in the PR description under **Requires human maintainer review**: what was chosen, the alternatives, and why. The maintainer accepts or redirects it there before merging.

## Record every choice once

- Settled answers: a comment on the ticket, citing the source.
- Choices needing review: the PR description, as above. Once the maintainer accepts one that changes a spec, ADR or glossary term, edit that too, so the next reader finds it there.
