# Assignment 2 – Reconciliation

A command-line batch tool in **C# (.NET 10)** that compares **List A** (merchant orders) with **List B** (invoices / settlement) on the unique reference **A.Order Number = B.Invoice Number**. It produces:

| File | Contents |
|---|---|
| **`Matched_Records.csv`** | References found in **both** lists, side by side. Each row is flagged `MATCHED` or `DISCREPANCY` and names the fields that differ, plus the amount and net differences |
| **`Missing_In_B.csv`** | Records in A that are absent from B |
| **`Missing_In_A.csv`** | Records in B that are absent from A |
| `Rejected_Records.csv` | Rows that failed validation: source, line number, every error, and the original row (with any full card number masked) |
| `Duplicate_References.csv` | References that occur more than once in the same list |
| `Reconciliation_Summary.json` | Counts and totals for each category, run time and peak memory |

The first three files are the required deliverables. The others are extras so that no input row is ever silently dropped.

---

## 1. Run it

**Prerequisite:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
cd Assignment2-Reconciliation

# Reconcile the two provided files (downloaded from the assignment's Google Sheets into data/)
dotnet run --project src/Reconciliation -c Release -- --list-a data/ListA.csv --list-b data/ListB.csv --output output

dotnet test        # 35 tests
```

Options (`--help` prints them):

```
--list-a <file>          List A CSV (unique reference column: "Order Number")
--list-b <file>          List B CSV (unique reference column: "Invoice Number")
--output <dir>           Output directory (default: ./output)
--date-format-a <fmt>    Date format in List A (default: yyyy-MM-dd)
--date-format-b <fmt>    Date format in List B (default: dd-MM-yyyy)
--partitions <n>         Number of hash partitions (default: derived from file size, ~4 MB each)
--temp-dir <dir>         Where partition files are spilled (default: system temp)

reconcile generate --rows <n> --output <dir>    # synthetic data for load testing
```

Exit codes: `0` success · `1` unexpected or I/O error · `2` bad arguments or missing file · `3` invalid input file (e.g. a missing column) · `130` cancelled (Ctrl+C).

## 2. Reviewer's guide: test everything in about 10 minutes

Each step lists exactly what to run and what you should see. Run every command from the `Assignment2-Reconciliation` folder.

**Before you start:** install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). To see a command's exit code afterwards, run `echo $LASTEXITCODE` in PowerShell or `echo $?` in bash.

### Step 1: Reconcile the provided files
```bash
dotnet run --project src/Reconciliation -c Release -- --list-a data/ListA.csv --list-b data/ListB.csv --output output
```
**Expected:** exit code `0`, and a summary showing
| | Count | Amount |
|---|---:|---:|
| Matched (exact) | 16 | 63,024.00 |
| Matched (with discrepancies) | 1 | 1,150.00 |
| Missing in B (only in A) | 7 | 31,657.50 |
| Missing in A (only in B) | 7 | 50,638.00 |

with `rejected: 0` and `duplicates: 0` for both lists.

### Step 2: The matched file, including the discrepancy
1. Open `output/Matched_Records.csv` (Excel is fine for *reading*).
2. **Expected:** 17 rows. 16 are `MATCHED`, and one is `DISCREPANCY`:
   - **`2696123`**, with *Discrepancy Fields* = `Amount; Net Total`
   - A Amount `1150.00` vs B Amount `3008.00`, *Amount Difference (A-B)* = `-1858.00`

   This reference exists in both lists, so it counts as matched, but its values differ. A key-only comparison would miss it.

### Step 3: The missing files
- `output/Missing_In_B.csv` → **7** orders: `2696128` to `2696134`.
- `output/Missing_In_A.csv` → **7** invoices: `99999`, `22025`, `12479`, `1111`, `47552`, `7841483`, `7782247`.
- `output/Reconciliation_Summary.json` → the same counts and totals. They tie out:
  - A 95,831.50 = 63,024.00 + 1,150.00 + 31,657.50
  - B 116,670.00 = 63,024.00 + 3,008.00 + 50,638.00

### Step 4: Bad rows are rejected, not fatal
1. Copy `data/ListA.csv` to `data/ListA-bad.csv` and open the copy in a **text editor** (Notepad or VS Code). Don't use Excel: it reformats dates and numbers when it saves.
2. Make three changes:
   - in the **`2696111`** line, change the date `2025-04-16` to `2025-13-45`
   - in the **`2696112`** line, change the amount `520.00` to `abc`
   - copy the **`2696113`** line and paste it at the end of the file
3. Run:
   ```bash
   dotnet run --project src/Reconciliation -c Release -- --list-a data/ListA-bad.csv --list-b data/ListB.csv --output output-bad
   ```
4. **Expected:** the run still completes with exit code `0`, and
   - **List A:** 25 rows read, 23 valid, 2 rejected, 1 duplicate
   - **Matched:** 14 exact + 1 discrepancy; **Missing in B:** 7
   - **Missing in A:** 9, because B's `2696111` and `2696112` no longer have a valid partner in A
   - `output-bad/Rejected_Records.csv` lists line 2 (*"Transaction Date must be a valid date in format yyyy-MM-dd."*) and line 3 (*"Amount must be a number…"*), each with the original row
   - `output-bad/Duplicate_References.csv` lists `2696113`: duplicate line 26, first seen on line 4

### Step 5: File-level errors are reported clearly
| Run | Expected |
|---|---|
| `dotnet run --project src/Reconciliation -c Release -- --list-a nope.csv --list-b data/ListB.csv` | `List A input file not found: …`, exit code `2` |
| `dotnet run --project src/Reconciliation -c Release -- --list-a data/ListB.csv --list-b data/ListB.csv --output output-wrong` | `List A: missing required column(s): 'Order Number'. Found: …`, exit code `3`, and **no report files** in `output-wrong` |

### Step 6: Performance on millions of rows (optional, needs about 2 GB of free disk)
```bash
dotnet run --project src/Reconciliation -c Release -- generate --rows 2000000 --output perf
dotnet run --project src/Reconciliation -c Release -- --list-a perf/ListA_2000000.csv --list-b perf/ListB_2000000.csv --output perf/out
```
**Expected:** about 1.8 million matched, 200,000 missing on each side, and a small number of deliberate discrepancies. The last lines show the elapsed time and peak memory. On the author's machine that was about 14 s and 106 MB; your timings will vary, but memory should stay in the low hundreds of MB. Delete the `perf` folder afterwards.

### Step 7: Run the automated tests
```bash
dotnet test
```
**Expected:** `Passed! - Failed: 0, Passed: 35`.

### Clean up
Delete `data/ListA-bad.csv`, `output-bad/`, `output-wrong/` and `perf/` if you created them. `output/` holds the results for the provided data and is regenerated by step 1.

## 3. Results for the provided data

The output of this run is committed in [`output/`](output/).

```
==================== RECONCILIATION SUMMARY ====================
List A   rows read:           24   valid:           24   rejected:        0   duplicates:        0
List B   rows read:           24   valid:           24   rejected:        0   duplicates:        0
----------------------------------------------------------------
Matched (exact)                         16   amount            63,024.00
Matched (with discrepancies)             1   amount             1,150.00
Missing in B (only in A)                 7   amount            31,657.50
Missing in A (only in B)                 7   amount            50,638.00
----------------------------------------------------------------
Result: DISCREPANCIES FOUND - review the files above.
```

* **17 matched references.** 16 agree on every field.
* **`2696123` is a matched reference with a discrepancy.** A records **1,150.00** (net 1,140.72) and B records **3,008.00** (net 2,998.72), a difference of −1,858.00. A plain "exists in both" check would have missed it. Flagging it is the point of comparing values and not only keys.
* **Missing in B:** orders `2696128`–`2696134` (7 orders, 31,657.50).
* **Missing in A:** invoices `99999`, `22025`, `12479`, `1111`, `47552`, `7841483`, `7782247` (7 invoices, 50,638.00). They don't look like the merchant's order-number format, which points to mis-keyed or foreign invoices.
* **Totals tie out.** A's 95,831.50 = 63,024.00 + 1,150.00 + 31,657.50. B's 116,670.00 = 63,024.00 + 3,008.00 + 50,638.00.

---

## 4. Design

### 4.1 Built for millions of rows: a two-phase partitioned hash join

Loading both files into memory does not scale. Sorting both files externally works but costs O(n log n) and a lot of extra I/O. This tool uses a **Grace hash join**:

```
Phase 1 – validate & partition (streaming, one row in memory at a time)
  ListA.csv ─► CSV reader ─► validator ─┬─► invalid ─► Rejected_Records.csv
                                        └─► valid ─► a-{hash(ref) % N}.csv   (temp)
  ListB.csv ─► (same) ──────────────────────────────► b-{hash(ref) % N}.csv  (temp)

Phase 2 – for each partition p = 0..N-1  (only ONE partition in memory)
  load b-p into Dictionary<reference, record>
  stream a-p:   found   → compare fields → Matched_Records.csv (MATCHED / DISCREPANCY)
                missing → Missing_In_B.csv
  leftover B in b-p      → Missing_In_A.csv
```

Equal references always hash to the same partition, so every match is found. The cost is **O(|A| + |B|)** time and roughly **O(partition size)** memory. N is chosen automatically, about 4 MB of input per partition, so memory stays flat however large the files grow. Other efficiency details:

* The CSV reader is hand-written: a streaming RFC 4180 parser over a 64 KB buffer that handles quoted commas such as `"3,900.00"`, escaped quotes and embedded newlines.
* Repeated values (`Status`, the masked card) share one string instance instead of allocating one per row.
* The partition hash is FNV-1a, which is stable across runs and platforms, unlike `string.GetHashCode`.
* `System.GC.ConserveMemory` makes the GC reclaim each finished partition's large arrays promptly.

**Measured** (Release build on a Windows 10 desktop, synthetic data from `reconcile generate`: 90% overlap, 1% deliberate amount mismatches):

| Rows per list | Input size (A + B) | Partitions | Time | Peak working set |
|---:|---:|---:|---:|---:|
| 24 (provided data) | 3.5 KB | 1 | 0.03 s | 28 MB |
| 2,000,000 | 306 MB | 42 | 14.1 s | **106 MB** |
| 10,000,000 | 1.5 GB | 211 | 69.2 s | **183 MB** |

At 10M rows per list, 4.5× the data cost only about 75 MB more memory, and time grows linearly. For comparison, the same 2M rows with only 6 partitions (≈28 MB each) peaked at 427 MB. Partition size is the memory knob.

### 4.2 Data integrity and validation

Each row is checked before it takes part in matching. **All** errors in a row are reported together, so the data owner can fix everything in one pass.

| Check | Rule |
|---|---|
| Header | Columns are located **by name**, not position, so reordered or extra columns work. A missing or duplicated required column stops the run (exit 3) before any output is written |
| Column count | Must equal the header's column count |
| Reference | Required, 1–50 characters of `A-Z a-z 0-9 - _ /` |
| `#` | A positive integer if present |
| Transaction Date | Exact format per file (A `yyyy-MM-dd`, B `dd-MM-yyyy`, both configurable). It must be a real calendar date (31-02 is rejected), no earlier than 2000-01-01 and no later than tomorrow |
| Amount | A number with correctly placed thousands separators (`3,900.00` passes, `3,90.00` fails), at most 2 dp, > 0 and ≤ 1,000,000,000 |
| Fees1 / Fees2 | Numbers ≤ 0 (they are deductions) whose absolute value does not exceed the Amount |
| **Net Total** | Must **equal Amount + Fees1 + Fees2 exactly**. This catches a row whose figures are inconsistent with each other |
| Status | `Success` / `Failed` / `Pending`, case-insensitive, normalised |
| Card Number (B) | Must be **masked** (`555555****1111`). A full 13–19 digit PAN is rejected as a PCI violation and is masked again before being written to the rejects file |
| Duplicates | A second occurrence of a reference in the same file goes to `Duplicate_References.csv`. The first occurrence is used for matching |

All numbers are parsed into `decimal`, never floating point, so money comparisons are exact.

### 4.3 Error handling
* **Row-level problems** (bad values, wrong column count, an unterminated quote) are written to `Rejected_Records.csv` and processing continues.
* **File-level problems** (missing file, bad header, an I/O error) produce one clear log message and a specific exit code. There is no stack trace for expected failures; unexpected exceptions are logged in full.
* **Atomic output.** Reports are written as `*.partial` and renamed only after the whole run succeeds. A crash or Ctrl+C never leaves a half-written `Matched_Records.csv` that looks complete. Temporary partition files are always deleted (tested).
* **CSV injection safety.** Output cells beginning with `= + - @` that are not numbers get a leading `'`, so a malicious value cannot run as a formula in Excel.

### 4.4 Output conventions
* Dates are normalised to ISO `yyyy-MM-dd` and amounts to plain `0.00` with no thousands separator, so A and B (which use different date formats) can be compared directly and the files load cleanly into other tools.
* Every output row carries its **source line number** for traceability back to the input.
* Row order: with one partition (any small file), output keeps input order. With several partitions, rows are grouped by partition. Sort in Excel if needed; sorting millions of rows would defeat the memory bound.

---

## 5. Code layout

```
src/Reconciliation/
├─ Program.cs                    entry point, exit codes, console summary
├─ Cli/CommandLineOptions.cs     argument parsing + help text
├─ Csv/CsvReader.cs, CsvWriter.cs streaming RFC 4180 reader/writer
├─ Model/                        TransactionRecord, SourceSchema (columns, date format), ColumnMap
├─ Validation/RecordValidator.cs field rules
├─ Processing/
│  ├─ ReconciliationEngine.cs    phase 1 (validate + partition) and phase 2 (hash join)
│  ├─ PartitionStore.cs          spill-to-disk hash partitions
│  ├─ ReportWriter.cs            output files, atomic commit
│  └─ ReconciliationSummary.cs / ReconciliationOptions.cs
└─ Generation/SampleDataGenerator.cs  large synthetic inputs for load tests
tests/Reconciliation.Tests/      CSV parsing, validation rules, end-to-end engine tests
data/                            the two provided lists (downloaded as CSV)
output/                          results for the provided data
```

Tests include: the exact expected result for the provided files; **identical output for 1 vs 37 partitions** on 5,000 generated rows, which proves partitioning never loses a match; rejected and duplicate handling; masking of an unmasked PAN; atomic failure on a bad header; and clean-up of temporary files.
