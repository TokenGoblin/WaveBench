#!/usr/bin/env python3
"""Fail if anything identifying the owner, their machine or their network has
reached a tracked file.

This repository is public. CLAUDE.md states the rule and says to scan the
staged diff before every commit, which until now meant remembering to. A rule
kept by diligence is a rule that holds until the one commit where somebody is
tired, and the cost of that commit is not recoverable: a force-push removes it
from the branch, not from anybody's clone, fork or the network's caches.

    python tools/check-sanitisation.py            # tracked files (what CI runs)
    python tools/check-sanitisation.py --history  # every blob in every commit

Exit code 1 on any finding. Every hit prints file:line and the matched text so
it can be JUDGED - several of these patterns have legitimate matches, and the
allow-list below explains each one rather than silencing it quietly.
"""
import argparse
import io
import os
import re
import subprocess
import sys

# ---- What must never appear ------------------------------------------------
PATTERNS = [
    ('absolute Windows path',
     re.compile(r'[A-Za-z]:[\\/](?:Users|Documents and Settings|Program Files)', re.I),
     'a drive-letter path into a user profile'),

    ('unix home path',
     re.compile(r'(?:/home/|/Users/)[A-Za-z0-9._-]+'),
     'a POSIX home directory'),

    ('UNC / network share',
     re.compile(r'\\\\[A-Za-z0-9_.-]+\\[A-Za-z0-9_$.-]+'),
     'a machine name and share'),

    ('private IP address',
     re.compile(r'\b(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b'),
     'a LAN address'),

    ('localhost endpoint',
     re.compile(r'\b(?:localhost|127\.0\.0\.1):\d+'),
     'a local service port'),

    ('user profile variable',
     re.compile(r'%(?:USERPROFILE|HOMEPATH|USERNAME)%', re.I),
     'expands to the owner account name'),

    ('email address',
     re.compile(r'[\w.+-]+@[\w-]+\.[\w.]+'),
     'any address that is not a public noreply alias'),

    ('GitHub token',
     re.compile(r'gh[pousr]_[A-Za-z0-9]{16,}'), 'a credential'),

    ('AWS access key',
     re.compile(r'AKIA[0-9A-Z]{16}'), 'a credential'),

    ('private key block',
     re.compile(r'-----BEGIN [A-Z ]*PRIVATE KEY-----'), 'a signing or transport key'),
]

# ---- Legitimate matches, each with the reason it is allowed ----------------
ALLOWED = [
    # The project's own published identities.
    (re.compile(r'42847007\+TokenGoblin@users\.noreply\.github\.com'),
     'the repository owner\'s GitHub noreply address - the identity every commit uses'),
    (re.compile(r'noreply@anthropic\.com'), 'commit co-author trailer'),
    (re.compile(r'noreply@github\.com'), 'GitHub web-commit identity'),

    # Documentation OF the rule has to be able to quote the thing it forbids.
    (re.compile(r'C:\\Users\\\.\.\.'), 'CLAUDE.md quoting the rule itself, with an ellipsis'),

    # Schema and namespace URIs that happen to look like addresses.
    (re.compile(r'https?://[^\s"\'<>]+'), 'a URL, not an email address'),

    # A scanner necessarily contains the shapes it hunts for. The escaped
    # source of the rule above reads as a machine name and share to the UNC
    # pattern, so this file reported itself. Only the inside of a regex
    # literal is exempt - a path anywhere else in this file is still a
    # finding, which is what stops the exemption becoming a hiding place.
    (re.compile(r"re\.compile\(\s*r?['\"].*?['\"]"),
     'a regex literal in this repository\'s own scanners, not a real path'),
]

BINARY_EXTENSIONS = {
    '.png', '.jpg', '.jpeg', '.gif', '.ico', '.pdf', '.wav', '.flac',
    '.db', '.sqlite', '.dll', '.exe', '.pdb', '.zip', '.msi', '.msix', '.cab',
}

# ---- File types that must never be TRACKED at all --------------------------
#
# Machine-generated files embed absolute paths even when nothing in the source
# does, so they are caught by extension rather than by scanning their contents.
# .pyc is on this list because a compiled Python module stores the ABSOLUTE
# path of its source in co_filename. One got committed by the very change that
# added this script - an unrelated debug import created tools/__pycache__ - and
# it carried the full profile path, username included. Nothing in the source
# leaked; the build artefact did.
FORBIDDEN_EXTENSIONS = {
    '.user', '.suo', '.pfx', '.p12', '.key', '.pem', '.env', '.log',
    '.db', '.sqlite', '.wav', '.flac', '.pyc', '.pyo',
}

FORBIDDEN_PATH_FRAGMENTS = (
    '/bin/', '/obj/', '/.vs/', '__pycache__/', 'BenchmarkDotNet.Artifacts/',
    'TestResults/', 'packaging/out/', 'packaging/publish/', 'packaging/staging/',
)


def run(*args):
    return subprocess.run(args, capture_output=True, check=True).stdout.decode('utf-8', 'replace')


def tracked_files():
    return [p for p in run('git', 'ls-files', '-z').split('\0') if p]


def allowed(line, start, end):
    """Whether a hit lies INSIDE something the allow-list permits.

    By span, not by text. Comparing the allow-rule against the hit itself does
    not work: a pattern matches the shortest thing it recognises, so the
    'absolute Windows path' rule reports 'C:\\Users' out of a line whose
    allowed form is the longer 'C:\\Users\\...', and the two never compare
    equal. Containment is the question actually being asked.
    """
    for rule, _ in ALLOWED:
        for match in rule.finditer(line):
            if match.start() <= start and end <= match.end():
                return True

    return False


def scan_text(rel, text, findings):
    for number, line in enumerate(text.splitlines(), 1):
        for name, pattern, note in PATTERNS:
            for match in pattern.finditer(line):
                if allowed(line, match.start(), match.end()):
                    continue
                findings.append((name, rel, number, match.group(0), line.strip()[:110], note))


def check_forbidden_paths(files, findings):
    for rel in files:
        lowered = rel.lower().replace('\\', '/')
        if os.path.splitext(lowered)[1] in FORBIDDEN_EXTENSIONS:
            findings.append(('forbidden file type', rel, 0, os.path.splitext(rel)[1],
                             'tracked file', 'machine-generated or secret-bearing; must stay untracked'))
        for fragment in FORBIDDEN_PATH_FRAGMENTS:
            if fragment.lower() in '/' + lowered:
                findings.append(('forbidden directory', rel, 0, fragment, 'tracked file',
                                 'build output; must stay untracked'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--history', action='store_true',
                        help='also search every blob in every commit (needs a full clone)')
    args = parser.parse_args()

    findings = []
    files = tracked_files()
    binaries = 0

    for rel in files:
        if not os.path.isfile(rel):
            continue
        extension = os.path.splitext(rel)[1].lower()
        if extension in BINARY_EXTENSIONS:
            binaries += 1
            continue
        try:
            scan_text(rel, io.open(rel, encoding='utf-8').read(), findings)
        except (UnicodeDecodeError, OSError):
            # NOT silently skipped. A tracked file this scanner cannot read is
            # a file nobody has checked, and unreadable binaries are exactly
            # where build tools hide absolute paths. Either it is a known
            # binary type above, or it is reported here.
            binaries += 1
            findings.append(('unreadable tracked file', rel, 0, extension or '(no extension)',
                             'not valid UTF-8 and not a known binary type',
                             'add it to BINARY_EXTENSIONS if it belongs, or untrack it'))

    check_forbidden_paths(files, findings)

    print('scanned %d tracked files (%d binary, checked by type not content)'
          % (len(files), binaries))

    if args.history:
        # Deleted files stay in history and stay public, so a clean working
        # tree proves nothing on its own.
        commits = run('git', 'rev-list', '--all').split()
        print('searching %d commits...' % len(commits))
        for name, pattern, note in PATTERNS:
            if name in ('email address',):
                continue  # too noisy across history; the working tree covers it
            result = subprocess.run(
                ['git', 'grep', '-I', '-n', '-E', pattern.pattern] + commits,
                capture_output=True)
            for line in result.stdout.decode('utf-8', 'replace').splitlines():
                if not any(rule.search(line) for rule, _ in ALLOWED):
                    findings.append((name + ' (history)', line[:90], 0, '', '', note))

    if not findings:
        print('PASS: nothing identifying the owner, their machine or their network')
        return 0

    print()
    for name, rel, number, hit, line, note in findings:
        where = '%s:%d' % (rel, number) if number else rel
        print('FAIL [%s] %s' % (name, where))
        print('       match: %r  (%s)' % (hit, note))
        if line:
            print('       line : %s' % line)

    print()
    print('%d finding(s). This repository is public: fix before committing.' % len(findings))
    return 1


if __name__ == '__main__':
    sys.exit(main())
