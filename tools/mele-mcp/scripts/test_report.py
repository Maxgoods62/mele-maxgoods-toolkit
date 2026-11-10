"""What scripts/test.sh shows of its dotnet runs, and whether every test ran.
  test_report.py LOG [TRX] [--allow-skips]
LOG is the console output of the lectool build and of dotnet test. It is printed without the build warnings of
external/LegendaryExplorer (LegendaryExplorerCore is noisy and not ours), with each of the toolkit's own build warnings
once (the first 30) and each NuGet audit warning (NU19xx) once.
TRX is dotnet test's result file. Its outcome names are the same in every language, and the console's are not: a test
that did not run (Assert.Inconclusive, [Ignore]) has the outcome NotExecuted there, and dotnet test still exits 0.
Exit 1 when TRX is given and a test did not run, none ran, or the file is missing, unless --allow-skips; else 0. A
failed test is dotnet test's own exit code, which test.sh keeps."""
import os
import re
import sys
import xml.etree.ElementTree as ET

LEC = re.compile(r"external[\\/]LegendaryExplorer[\\/]", re.I)
WARNING = re.compile(r": warning( \w+)? ?:")
AUDIT = re.compile(r": warning (NU19\d\d): (.*?)(?: \[[^\]]*\])?$")
RAN = {"Passed", "Failed", "Error", "Timeout", "Aborted"}
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
SHOWN = 30


def show_log(path):
    """Prints the log as the module's docstring says."""
    seen, own = set(), 0
    with open(path, encoding="utf-8", errors="replace") as f:
        lines = f.read().replace("\r", "").split("\n")
    for line in lines:
        audit = AUDIT.search(line)
        if audit:
            line = f"NuGet audit {audit.group(1)}: {audit.group(2)}"
        elif WARNING.search(line):
            if LEC.search(line):
                continue
        else:
            if not line.rstrip().endswith("test.trx"):  # the result file's path, a machine path
                print(line)
            continue
        if line in seen:
            continue
        seen.add(line)
        if not audit:
            own += 1
            if own > SHOWN:
                continue
        print(line)
    if own > SHOWN:
        print(f"test.sh: and {own - SHOWN} more build warnings in the toolkit's own code")


def reason(result):
    """Why a test did not run, without the words MSTest puts before the message in the machine's language."""
    message = result.find(f"{NS}Output/{NS}ErrorInfo/{NS}Message")
    text = " ".join((message.text if message is not None and message.text else "no reason given").split())
    return re.sub(r"\.\.$", ".", re.sub(r"^.*Assert\.Inconclusive\S*\s+(failed\.\s+)?", "", text))


def skipped(path, allow):
    """Prints the tests that did not run, counted by reason; returns the exit code."""
    if not os.path.isfile(path):
        if allow:
            return 0
        print("test.sh: FAILED: dotnet test wrote no result file, so no test ran")
        return 1
    results = list(ET.parse(path).getroot().iter(f"{NS}UnitTestResult"))
    not_run = [r for r in results if r.get("outcome") not in RAN]
    if results and not not_run:
        return 0
    if not results:
        print("test.sh: no test ran")
    else:
        reasons = {}
        for r in not_run:
            reasons[reason(r)] = reasons.get(reason(r), 0) + 1
        print(f"test.sh: {len(not_run)} of {len(results)} tests did not run:")
        for text, n in sorted(reasons.items(), key=lambda kv: (-kv[1], kv[0])):
            print(f"  {n} x {text}")
    if allow:
        return 0
    print("test.sh: FAILED: a test that did not run proves nothing. Give the tests what they miss, or pass "
          "--allow-skips to accept this run.")
    return 1


def main(argv):
    allow = "--allow-skips" in argv
    paths = [a for a in argv if a != "--allow-skips"]
    if not 1 <= len(paths) <= 2:
        print(__doc__, file=sys.stderr)
        return 2
    show_log(paths[0])
    return skipped(paths[1], allow) if len(paths) == 2 else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
