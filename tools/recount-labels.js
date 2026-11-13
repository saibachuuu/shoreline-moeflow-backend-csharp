// Rebuild the cached label counters for every project that drifted.
//
// The counters (file.tsc / target.tsc / project.tsc) are maintained by
// incremental +1/-1 updates, so a single skipped update leaves a permanent
// drift. This script recomputes them from the translation collection, using
// exactly the same rules as LabelCounterReconciler.CountTranslationsAsync:
//
//   translated = distinct (file, source) pairs having at least one translation
//                whose source is not blank
//   checked    = the same, counting only translations with selected=true
//
// Run read-only first (default), then with --apply to write.
//
//   mongo -u USER -p PASS --authenticationDatabase admin moeflow \
//         --quiet tools/recount-labels.js
//   mongo ... --eval 'var APPLY=true' tools/recount-labels.js
//
// Pass a project id to limit the scope:
//   mongo ... --eval 'var ONLY="6aeed79af54dd6b6f8b27c17"' tools/recount-labels.js

var APPLY = (typeof APPLY === "undefined") ? false : APPLY;
var ONLY = (typeof ONLY === "undefined") ? null : ONLY;
var FOLDER = 1;

var projectFilter = ONLY ? {_id: ObjectId(ONLY)} : {};
var projects = db.project.find(projectFilter, {_id: 1, n: 1, tsc: 1, csc: 1}).toArray();
print("projects=" + projects.length + " apply=" + APPLY);

var drifted = 0;

projects.forEach(function(project) {
  var pid = project._id;
  var targets = db.target.find({t: pid}, {_id: 1, tsc: 1, csc: 1}).toArray();
  if (!targets.length) return;

  var perCell = {};   // "fileId|targetId" -> {ts, cs}
  var targetSum = {};
  targets.forEach(function(t) { targetSum[String(t._id)] = {ts: 0, cs: 0}; });

  targets.forEach(function(target) {
    var rows = db.translation.aggregate([
      {$match: {t: target._id}},
      {$lookup: {from: "source", localField: "o", foreignField: "_id", as: "src"}},
      {$unwind: "$src"},
      {$match: {"src.b": {$ne: true}}},
      {$lookup: {from: "file", localField: "src.f", foreignField: "_id", as: "file"}},
      {$unwind: "$file"},
      {$match: {"file.p": pid}},
      {$group: {_id: {f: "$file._id", o: "$o"},
                checked: {$max: {$cond: ["$s", 1, 0]}}}},
      {$group: {_id: "$_id.f", ts: {$sum: 1}, cs: {$sum: "$checked"}}}
    ]).toArray();
    rows.forEach(function(row) {
      perCell[String(row._id) + "|" + String(target._id)] = {ts: row.ts, cs: row.cs};
      targetSum[String(target._id)].ts += row.ts;
      targetSum[String(target._id)].cs += row.cs;
    });
  });

  var projectTs = 0, projectCs = 0;
  var changed = false;

  db.file.find({p: pid}, {_id: 1, t: 1, ac: 1, tsc: 1, csc: 1}).forEach(function(file) {
    var isFolder = (file.t === FOLDER);
    var ts = 0, cs = 0;
    targets.forEach(function(target) {
      var cell = perCell[String(file._id) + "|" + String(target._id)];
      var cellTs = cell ? cell.ts : 0;
      var cellCs = cell ? cell.cs : 0;
      ts += cellTs;
      cs += cellCs;

      var cache = db.file_target_cache.findOne({f: file._id, t: target._id});
      if (cache) {
        if ((cache.ts || 0) !== cellTs || (cache.cs || 0) !== cellCs) {
          if (APPLY) db.file_target_cache.updateOne({_id: cache._id}, {$set: {ts: cellTs, cs: cellCs}});
          changed = true;
        }
      }
    });
    if (isFolder) return;
    if (ts !== (file.tsc || 0) || cs !== (file.csc || 0)) {
      print("  file " + file._id + " tsc " + (file.tsc || 0) + "->" + ts + " csc " + (file.csc || 0) + "->" + cs);
      if (APPLY) db.file.updateOne({_id: file._id}, {$set: {tsc: ts, csc: cs}});
      changed = true;
    }
    if (file.ac === true) { projectTs += ts; projectCs += cs; }
  });

  targets.forEach(function(target) {
    var sum = targetSum[String(target._id)];
    if (sum.ts !== (target.tsc || 0) || sum.cs !== (target.csc || 0)) {
      print("  target " + target._id + " tsc " + (target.tsc || 0) + "->" + sum.ts + " csc " + (target.csc || 0) + "->" + sum.cs);
      if (APPLY) db.target.updateOne({_id: target._id}, {$set: {tsc: sum.ts, csc: sum.cs}});
      changed = true;
    }
  });

  if (projectTs !== (project.tsc || 0) || projectCs !== (project.csc || 0)) {
    print("  project " + pid + " tsc " + (project.tsc || 0) + "->" + projectTs + " csc " + (project.csc || 0) + "->" + projectCs);
    if (APPLY) db.project.updateOne({_id: pid}, {$set: {tsc: projectTs, csc: projectCs}});
    changed = true;
  }

  if (changed) drifted++;
});

print("driftedProjects=" + drifted);
if (!APPLY && drifted > 0) print("re-run with APPLY=true to write the corrections");
