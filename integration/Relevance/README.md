# Local Relevance integration

Run `dotnet run --project integration/Relevance -p:RelevanceSourceRoot=/path/to/mostlylucidweb` from the Packing repository. The separate SDK must include `RankCandidatesAsync`.

The Relevance engine owns converters, evidence decay, mandatory eligibility and rank fusion. The Packing bridge supplies feasible appointments and maps scores through its callback. No temporary candidates enter the stored catalogue, and this read creates no exposure or feedback record. The host must record real exposures/outcomes separately and recheck capacity when holding a chosen appointment.

Decision record:

```yaml
entity: salon customer alice
candidate: feasible consult appointment with a technician
authorised_scope: local single-salon demonstration; production host resolves tenant and customer
representation: appointment-fit-v1; UTC minute-of-day / 1440 and Sarah indicator (0 or 1); reject missing attributes
observation: visit-1, alice, past-visit; demonstration clock
objective: similarity to observed preference; scores are not booking probabilities
retrieval: exact over the full bounded feasible pool
eligibility: Packing service/client/role/capacity checks, then Relevance placement rules
storage: MemoryRelevanceStore; no durable sharding in this example
verification: learned best slot, hold exclusion, block delta, cold start, no transient persistence
```

The two coordinates are deliberately simple teaching features, not a production preference model. Changing their meaning requires a new representation version. A real application should measure held-out acceptance and cancellations before using learned ranking for suggestions.
