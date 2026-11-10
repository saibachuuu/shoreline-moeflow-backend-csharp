# MongoDB 4.4 compatibility

The Python production stack still runs `mongo:4.4.1`. The C# rewrite must work
against that server, not only against the newer Mongo used by local tests.

## Constraint

MongoDB 4.4 rejects `$lookup` stages that combine `localField`/`foreignField`
with a `pipeline` sub-stage. MongoDB 5.0 added that combination. The error
surfaces as a generic `MongoCommandException` and a 500 response, so it is easy
to miss in local integration tests if they use a newer server.

Use the `let` form instead:

```javascript
{
  $lookup: {
    from: "user",
    let: { uid: "$user" },
    pipeline: [
      { $match: { $expr: { $eq: ["$_id", "$$uid"] } } },
      { $project: { n: 1, ns: 1, asrch: 1, /* ... */ } }
    ],
    as: "csharp_list_user"
  }
}
```

## Fixed call sites (2026-11-10)

- `src/Shoreline.Api/Features/TeamMemberService.cs` `ListAsync`: team member
  list joined `user` with `localField` + `pipeline`, causing
  `GET /v1/teams/<id>/members` to return 500 on the test machine after the
  second member joined. Converted to `let` + `$expr` + `$project`.
- `src/Shoreline.Api/Features/IdentityTagPolicyService.cs` `InUseAsync`:
  project-member lookup used the same unsupported combination. Converted to
  `let` + `$match`/`$project`.

After the fix, the test-machine member list returned both members and the
remaining team invitation flow passed (see the production checklist).

## Other 4.4 caveats checked

- No `$setWindowFields`, `$function`, `$accumulator`, `$densify`, `$fill`,
  `$documents`, `$merge`, or `$out` usage in `src/Shoreline.Api`.
- `PartialFilterExpression` indexes, `$expr`, `$facet`, and `$lookup` without
  a combined `localField` are supported by 4.4.
- The local test Mongo is 8.x; run at least the deployment smoke against the
  real 4.4 test-machine stack after any aggregation change.
