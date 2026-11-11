// Read-only cutover pre-check: projects whose storage source is missing or
// disabled.
//
// Why this matters before switching Python -> C#:
//   Python's storage_router returns None and callers fall back to the global
//   default bucket (app/services/storage_router.py, file.py `... or oss`).
//   The C# ProjectStorageRouter fails closed for a non-empty `csid` that does
//   not resolve, so those projects return 409/6 instead of silently reading a
//   possibly stale copy from the default bucket.
//
// This script never writes. Run it against a production copy before cutover:
//   mongo "mongodb://user:pass@host:27017/moeflow?authSource=admin" \
//         tools/check-storage-sources.js
//
// Exit guidance: healthy rows are unaffected. `missingSource`/`disabledSource`
// must be repaired (re-point `csid`, re-enable the source) or explicitly
// accepted before cutover; otherwise those projects 409 on C# while Python
// served them (stale) from the default bucket.

const sources = db.getCollection("storage_pool_source");
const result = {
  sources: sources.countDocuments({}),
  disabledSources: sources.countDocuments({ e: false }),
  projectsWithSource: 0,
  healthy: 0,
  missingSource: 0,
  disabledSource: 0,
  samples: [],
};

db.getCollection("project")
  .find({ csid: { $exists: true, $ne: null } })
  .forEach((project) => {
    result.projectsWithSource += 1;
    // Compare BSON values directly (one indexed _id lookup per project).
    // String() is shell-version dependent: some shells render
    // ObjectId("…") while others return the bare hex string.
    const source = sources.findOne({ _id: project.csid });
    let status = "healthy";
    if (!source) status = "missingSource";
    else if (source.e === false) status = "disabledSource";

    if (status === "healthy") {
      result.healthy += 1;
      return;
    }
    result[status] += 1;
    if (result.samples.length < 20) {
      result.samples.push({
        project: project._id,
        name: project.n,
        csid: project.csid,
        status,
      });
    }
  });

printjson(result);

const blocked = result.missingSource + result.disabledSource;
if (blocked > 0) {
  print(
    "WARNING: " +
      blocked +
      " project(s) fail closed (409/6) on the C# backend. The Python backend " +
      "silently falls back to the default bucket. Repair or accept before cutover."
  );
} else {
  print("OK: every project with a storage source resolves to an enabled source.");
}
