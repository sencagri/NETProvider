# FirebirdSql.Data.DomainTypeMapping

Wrapper around `FirebirdSql.Data.FirebirdClient` that reports columns whose Firebird **domain name** (`RDB$FIELD_SOURCE`) matches a configured pattern as `Boolean` or `Guid` to ADO.NET — even when the underlying SQL type is `SMALLINT` or `CHAR(16) OCTETS`.

This is the .NET-side equivalent of LCPI IBProvider's `user_type_boolean=...` / `user_type_guid=...` connection-string switches, intended for codebases migrating from IBProvider where domains were used to encode CLR semantics.

The wrapper is **layered on top of** `FirebirdSql.Data.FirebirdClient`. The core provider is **not modified**.

---

## Quick start

```csharp
using FirebirdSql.Data.DomainTypeMapping;
using FirebirdSql.Data.FirebirdClient;

var options = new DomainTypeMappingOptions
{
    BooleanDomains = "D_BOOL%,IS\\_%",   // SQL LIKE patterns, comma-separated
    GuidDomains    = "D_GUID%",
};

await using var conn = new FbDomainConnection(connectionString, options);
await conn.OpenAsync();

await using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT ID, IS_ACTIVE, ROW_GUID FROM USER_TYPE_TEST WHERE ID = @id";
cmd.Parameters.Add("@id", 1);

await using var reader = await cmd.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    int id            = reader.GetInt32(0);
    bool isActive     = reader.GetBoolean(1);  // SMALLINT under the hood
    Guid rowGuid      = reader.GetGuid(2);     // CHAR(16) OCTETS under the hood

    // schema reports the right CLR types too:
    Console.WriteLine(reader.GetFieldType(1)); // System.Boolean
    Console.WriteLine(reader.GetFieldType(2)); // System.Guid
}
```

Inserting works just as transparently:

```csharp
cmd.CommandText = "INSERT INTO USER_TYPE_TEST (ID, IS_ACTIVE, ROW_GUID) VALUES (@id, @act, @g)";
cmd.Parameters.Add("@id", 1);
cmd.Parameters.Add("@act", true);             // bool -> SMALLINT
cmd.Parameters.Add("@g", Guid.NewGuid());     // Guid -> CHAR(16) OCTETS
await cmd.ExecuteNonQueryAsync();
```

---

## Pattern syntax

Patterns are SQL `LIKE` expressions, comma-separated:

| Token | Meaning |
| --- | --- |
| `%`   | matches any sequence of characters |
| `_`   | matches a single character |
| `\_`  | a literal underscore |
| any other character | matched literally |

Matching is case-insensitive. System domains (`RDB$*`) are never matched. Examples:

| Spec | Matches |
| --- | --- |
| `D_BOOL%`              | `D_BOOL`, `D_BOOL_FLAG`, `D_BOOLEAN` |
| `D_BOOL%,IS\_%`        | the above, plus `IS_ACTIVE`, `IS_DELETED`, … |
| `MY__FLAG`             | `MY1FLAG`, `MYXFLAG` (single-char wildcard) |
| `D\_BOOL`              | only the literal `D_BOOL` |

---

## How it works

```
your code
   │
   ▼                       (public API)
FbDomainConnection ──── wraps ────▶ FbConnection
FbDomainCommand    ──── wraps ────▶ FbCommand
FbDomainDataReader ──── wraps ────▶ FbDataReader
```

* **Reading.** When a `FbDomainDataReader` is opened, it inspects `GetSchemaTable()` from the inner reader to learn each column's `BaseTableName` / `BaseColumnName`. A single batched `SELECT … FROM RDB$RELATION_FIELDS WHERE …` resolves the `RDB$FIELD_SOURCE` for any (relation, field) the connection has not seen before. Results are cached for the connection's lifetime. Columns whose domain matches `BooleanDomains` / `GuidDomains` get `bool` / `Guid` reported by `GetFieldType`, `GetSchemaTable`, `GetValue`, `GetBoolean`, `GetGuid`. All other readers / methods delegate to the inner reader unchanged.

* **Writing.** When a command with at least one `bool` or `Guid` parameter value is executed, the wrapper calls `Prepare()` on the inner command to populate the wire descriptor, then reflects into `FbCommand._statement.Parameters` to read each parameter's `SqlType`. If the wire wants `SMALLINT` / `INTEGER` / `BIGINT` and the user supplied a `bool`, the value is replaced with the matching numeric. Real `BOOLEAN` parameters are left alone; `Guid` to `CHAR(16) OCTETS` is delegated to the provider's existing `FbDbType.Guid` path.

* **Opt-in.** When neither `BooleanDomains` nor `GuidDomains` is set, the wrapper short-circuits in every code path (no `RDB$RELATION_FIELDS` query, no reflection, no schema rewrite) and behaves identically to a plain `FbConnection`.

---

## Reflection caveat

The parameter-conversion path on writes uses **reflection** into private fields of `FbCommand` (`_statement`) and the internal `Descriptor` / `DbField` types. These are stable across the provider's recent releases, but they are not part of its public contract. If a future provider release renames or restructures these members, the reflection probe **fails closed**: the wrapper skips the conversion, and you get the same exception you would have without the wrapper installed (better than silently sending wrong values).

The read path uses only public ADO.NET APIs (`GetSchemaTable`, `BaseTableName`, `BaseColumnName`) and is not affected by provider internals.

---

## Typed DataSet code generation

For typed DataSets to be generated with `bool` / `Guid` columns, the codegen has to use a `FbDomainConnection`. Two workflows:

### Option A — CLI codegen tool

Write a small .NET program that drives `FillSchema` against the wrapper and writes XSDs:

```csharp
await using var conn = new FbDomainConnection(connectionString, options);
await conn.OpenAsync();

var ds = new DataSet("MyDb");
foreach (var table in tablesToGenerate)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT * FROM {table}";
    var adapter = new FbDataAdapter();
    adapter.SelectCommand = cmd.InnerCommand;       // for FbDataAdapter
    var dt = new DataTable(table);
    using var reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
    var schema = reader.GetSchemaTable();
    BuildDataTableFromSchema(dt, schema);           // your code: maps schema → columns
    ds.Tables.Add(dt);
}
ds.WriteXmlSchema("MyDb.xsd");
```

Then `xsd.exe MyDb.xsd /d /l:cs /n:MyApp.Data` produces the typed DataSet.

### Option B — XSD post-processing

If you already have generated XSDs, run a one-time script that rewrites column types based on your domain config (see the project README for an example). Runtime usage **still requires the wrapper** because the typed DataSet's `bool` columns need values that come from the wrapper reader.

---

## Limitations

* **Schema changes during a connection's lifetime are not picked up.** The cache is populated lazily and not invalidated. Open a fresh connection if domains have been altered.

* **Computed columns / function results** have no `BaseTableName` and cannot be domain-mapped. They're returned with the underlying CLR type as usual.

* **Stored-procedure output** with a domain-typed declared parameter currently isn't covered by `BaseTableName`/`BaseColumnName` either; sproc result columns appear as their wire type.

* **Reflection drift.** See above. Pin the `FirebirdSql.Data.FirebirdClient` version you've validated against, and re-test when upgrading.

---

## License

Initial Developer's Public License 1.0 — same as the upstream Firebird .NET provider.
