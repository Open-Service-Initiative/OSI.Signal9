# Decisions and maintainer review

Who settles a question that comes up while building. Applies to agents and people alike: a coordinator answering a worker, an implementer stuck on a ticket, a reviewer.

## Settled: answer it and cite the source

If the answer is in one of these, it's already decided. Answer from it and name where it came from.

- The spec issue and the ticket.
- `docs/adr/`, `GLOSSARY.md` and the API conventions in `AGENTS.md`.
- Anything findable in the code, the docs or by running something. Finding facts is never a reason to stop.

## Requires human maintainer review

Stop and ask a human maintainer when an answer would:

- change a spec, an ADR or a glossary term;
- make an architecture or high-level design choice the sources above don't already make;
- close a one-way door: public API shape, database schema, security or authorization behaviour, a new dependency or licence;
- go beyond the ticket's scope.

Until a maintainer answers, leave that part undone and say what's blocked; build the rest.

## Record every answer once

- Post the answer as a comment on the ticket, citing its source or the maintainer who decided.
- If it changes a spec, ADR or glossary term, edit that too, so the next reader finds it there.
