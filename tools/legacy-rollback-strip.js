// Remove data written only by the C# build before rolling back to Python.
//
// The Python 1.2.x MongoEngine models reject unknown BSON fields with
// FieldDoesNotExist, so a database written by C# must be cleaned before the
// Python application starts. Stop the C# backend first: this script does not
// coordinate with running workers.
//
// Usage (MongoDB 4.4 shell):
//   mongo "mongodb://user:pass@host:27017/moeflow?authSource=admin" \
//         tools/legacy-rollback-strip.js
//
// The database name comes from the connection string. The script is
// idempotent; re-running it after a fresh C# start repeats the cleanup.

const specs = {
  project: ["label_revision", "label_recovery"],
  file: ["label_epoch"],
  source: ["label_epoch"],
  target: ["label_epoch"],
  translation: ["label_epoch"],
  file_target_cache: ["label_epoch"],
  output: [
    "job_id",
    "job_owner",
    "job_lease_until",
    "job_run_token",
    "job_started_at",
    "job_completed_at",
    "job_error",
  ],
  team: ["m_u", "lc_phase", "lc_owner", "lc_started", "lc_updated", "lc_error"],
  ziteng_check: ["generation", "lease_owner", "lease_until"],
};

for (const collection of Object.keys(specs).sort()) {
  const unset = {};
  for (const field of specs[collection]) unset[field] = "";
  const result = db.getCollection(collection).updateMany({}, { $unset: unset });
  print(
    collection +
      ": matched " +
      result.matchedCount +
      ", modified " +
      result.modifiedCount
  );
}

// Earlier C# builds created some indexes under descriptive names while
// MongoEngine expects its generated names (or vice versa). MongoDB refuses
// to create the same key under a second name, so drop the C# variants and
// let the Python application recreate its own indexes at startup.
const legacyIndexes = {
  team: ["csharp_team_name_unique_v1"],
  team_member: [
    "team_member_status_v1",
    "team_member_aliases_v1",
    "team_member_worker_qualifications_v1",
  ],
  file: [
    "file_activated_name_parent_project",
    "file_type_sort_name",
    "file_type_sort_name_desc",
    "file_dir_type_sort_name",
    "file_dir_type_sort_name_desc",
    "t_1_-sn_1",
    "dn_1_t_1_-sn_1",
  ],
  file_target_cache: ["file_target_cache_file_target"],
  identity_operation: ["identity_operation_project_operation_v1"],
  project_member: ["project_member_display_name_search_v1"],
  ziteng_work: ["ziteng_work_id_v1", "ziteng_work_state_v1"],
  ziteng_query: ["ziteng_query_keyword_v1"],
  ziteng_check: ["ziteng_check_project_id_v1", "ziteng_check_status_v1"],
};

for (const collection of Object.keys(legacyIndexes).sort()) {
  for (const name of legacyIndexes[collection]) {
    try {
      db.getCollection(collection).dropIndex(name);
      print(collection + ": dropped index " + name);
    } catch (error) {
      // IndexNotFound / NamespaceNotFound: nothing to migrate.
      if (error.code !== 26 && error.code !== 27) throw error;
    }
  }
}

// Migration ledger rows written by the C# runner carry runner:"csharp" and a
// canonical checksum the Python AST hasher cannot reproduce. Python's
// _checksum_matches treats rows for v>=0004 as tampered and refuses to start.
// Delete only C#-written rows (rows Python wrote itself never have this
// runner tag), then clear the lock. Every Python up() is idempotent by design,
// so Python re-applies exactly the versions it never recorded.
const csharpMigrations = db
  .getCollection("migration_record")
  .deleteMany({ runner: "csharp" });
print(
  "migration_record: deleted " + csharpMigrations.deletedCount + " csharp rows"
);
const lock = db.getCollection("migration_lock").deleteMany({ _id: "migrate" });
print("migration_lock: cleared " + lock.deletedCount + " lease(s)");

