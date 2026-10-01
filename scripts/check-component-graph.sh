#!/usr/bin/env bash
# Verify that each output assembly name comes from exactly one project in the
# solution's restore graph. External Broiler components are NuGet dependencies;
# Hosting projects must not reintroduce duplicate source checkouts.
# Usage: scripts/check-component-graph.sh [solution ...]
# Defaults to root .slnx files; CONFIGURATION defaults to Debug.
# Requires dotnet and python3. This evaluates projects without building them.

set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

if [ "$#" -gt 0 ]; then
  solutions=("$@")
else
  mapfile -t solutions < <(find . -maxdepth 1 -name '*.slnx' -printf '%f\n' | sort)
fi

if [ "${#solutions[@]}" -eq 0 ]; then
  echo "::error::No solutions found to check. This cannot pass vacuously."
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# GitHub renders ::error annotations; a local run should not be shouted at in
# workflow-command syntax it cannot use.
annotate() {
  if [ -n "${GITHUB_ACTIONS:-}" ]; then
    echo "::error::$1"
  else
    echo "ERROR: $1" >&2
  fi
}

configuration="${CONFIGURATION:-Debug}"

status=0
checked_total=0
duplicates_found=0

for solution in "${solutions[@]}"; do
  echo "── $solution ($configuration)"

  dg="$work/dg.json"
  if ! dotnet msbuild "$solution" -t:GenerateRestoreGraphFile -p:Configuration="$configuration" \
        -p:RestoreGraphOutputPath="$dg" -nologo -v:q >"$work/dg.log" 2>&1; then
    annotate "$solution: could not evaluate the project graph."
    sed 's/^/    /' "$work/dg.log" >&2
    status=1
    continue
  fi

  probe="$work/probe.proj"
  python3 - "$dg" "$probe" >"$work/in_graph.txt" <<'PY' || { annotate "$solution: could not read the project graph."; status=1; continue; }
import json, sys, xml.sax.saxutils as x

dg, out = sys.argv[1], sys.argv[2]
graph = json.load(open(dg, encoding="utf-8"))
paths = sorted({(p.get("restore") or {}).get("projectPath") or key
                for key, p in graph.get("projects", {}).items()})

items = "\n".join('    <ProjectToProbe Include=%s />' % x.quoteattr(p) for p in paths)
open(out, "w", encoding="utf-8").write(f"""<Project>
  <ItemGroup>
{items}
  </ItemGroup>
  <Target Name="Probe">
    <MSBuild Projects="@(ProjectToProbe)"
             Targets="GetTargetPath"
             BuildInParallel="true"
             SkipNonexistentProjects="false"
             SkipNonexistentTargets="true"
             Properties="Configuration=$(Configuration)">
      <Output TaskParameter="TargetOutputs" ItemName="Probed" />
    </MSBuild>
    <WriteLinesToFile File="$(ProbeOutput)"
                      Lines="@(Probed->'%(Filename)%(Extension)|%(MSBuildSourceProjectFile)')"
                      Overwrite="true" />
  </Target>
</Project>
""")
print(len(paths))
PY

  in_graph="$(cat "$work/in_graph.txt")"

  names="$work/names.txt"
  : >"$names"
  if ! dotnet msbuild "$probe" -t:Probe \
        -p:Configuration="$configuration" -p:ProbeOutput="$names" \
        -nologo -v:q >"$work/probe.log" 2>&1; then
    annotate "$solution: could not resolve the output assembly names."
    sed 's/^/    /' "$work/probe.log" >&2
    status=1
    continue
  fi

  checked="$(grep -c . "$names" 2>/dev/null || echo 0)"
  if [ "$checked" -eq 0 ]; then
    annotate "$solution: no projects were probed. This check cannot pass vacuously."
    status=1
    continue
  fi
  checked_total=$((checked_total + checked))

  duplicated="$(cut -d'|' -f1 "$names" | sort | uniq -d)"
  if [ -z "$duplicated" ]; then
    printf '   ok  %d of %d projects probed, %d distinct assemblies\n' \
      "$checked" "$in_graph" "$(cut -d'|' -f1 "$names" | sort -u | grep -c .)"
    continue
  fi

  status=1
  duplicates_found=1
  while IFS= read -r assembly; do
    [ -n "$assembly" ] || continue
    annotate "$solution builds $assembly from more than one project. Keep one project per assembly and consume external components through packages."
    awk -F'|' -v a="$assembly" '$1 == a {print $2}' "$names" | sort -u | sed 's|^|        |'
  done <<<"$duplicated"
done

echo
if [ "$status" -eq 0 ]; then
  echo "One assembly per name across ${#solutions[@]} solution(s), $checked_total projects."
elif [ "$duplicates_found" -eq 1 ]; then
  echo "Duplicate assemblies found. See the errors above."
else
  echo "A solution could not be evaluated, so nothing was proved about it. See the errors above."
fi
exit "$status"
