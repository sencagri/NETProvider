/*
 *    The contents of this file are subject to the Initial
 *    Developer's Public License Version 1.0 (the "License");
 *    you may not use this file except in compliance with the
 *    License. You may obtain a copy of the License at
 *    https://github.com/FirebirdSQL/NETProvider/raw/master/license.txt.
 *
 *    Software distributed under the License is distributed on
 *    an "AS IS" basis, WITHOUT WARRANTY OF ANY KIND, either
 *    express or implied. See the License for the specific
 *    language governing rights and limitations under the License.
 *
 *    All Rights Reserved.
 */

//$Authors = Ebubekir Cagri Sen (ebubekircagrisen@gmail.com)

using System;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace FirebirdSql.Data.DomainTypeMapping;

public sealed class FbDomainDataReader : DbDataReader
{
	private readonly DbDataReader _inner;
	private readonly FbDomainConnection _connection;
	private DomainKind[] _columnKinds;
	private bool _columnKindsResolved;

	internal FbDomainDataReader(DbDataReader inner, FbDomainConnection connection)
	{
		_inner = inner;
		_connection = connection;
	}

	public override int FieldCount => _inner.FieldCount;
	public override int Depth => _inner.Depth;
	public override bool HasRows => _inner.HasRows;
	public override bool IsClosed => _inner.IsClosed;
	public override int RecordsAffected => _inner.RecordsAffected;
	public override int VisibleFieldCount => _inner.VisibleFieldCount;
	public override object this[int ordinal] => GetValue(ordinal);
	public override object this[string name] => GetValue(GetOrdinal(name));

	public override bool Read() => _inner.Read();
	public override Task<bool> ReadAsync(CancellationToken cancellationToken) => _inner.ReadAsync(cancellationToken);

	public override bool NextResult() => _inner.NextResult();
	public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _inner.NextResultAsync(cancellationToken);

	public override void Close() => _inner.Close();
	public override Task CloseAsync() => _inner.CloseAsync();

	public override string GetName(int ordinal) => _inner.GetName(ordinal);
	public override int GetOrdinal(string name) => _inner.GetOrdinal(name);
	public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

	public override Type GetFieldType(int ordinal)
	{
		switch (GetColumnKind(ordinal))
		{
			case DomainKind.Boolean:
				return typeof(bool);
			case DomainKind.Guid:
				return typeof(Guid);
			default:
				return _inner.GetFieldType(ordinal);
		}
	}

	public override object GetValue(int ordinal)
	{
		if (_inner.IsDBNull(ordinal))
			return DBNull.Value;
		switch (GetColumnKind(ordinal))
		{
			case DomainKind.Boolean:
				return GetBoolean(ordinal);
			case DomainKind.Guid:
				return GetGuid(ordinal);
			default:
				return _inner.GetValue(ordinal);
		}
	}

	public override int GetValues(object[] values)
	{
		if (values == null)
			throw new ArgumentNullException(nameof(values));
		var n = Math.Min(values.Length, FieldCount);
		for (var i = 0; i < n; i++)
			values[i] = GetValue(i);
		return n;
	}

	public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);
	public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => _inner.IsDBNullAsync(ordinal, cancellationToken);

	public override bool GetBoolean(int ordinal)
	{
		if (GetColumnKind(ordinal) == DomainKind.Boolean)
		{
			// The wire still has a numeric value; pull it as the underlying type.
			var fieldType = _inner.GetFieldType(ordinal);
			if (fieldType == typeof(short)) return _inner.GetInt16(ordinal) != 0;
			if (fieldType == typeof(int)) return _inner.GetInt32(ordinal) != 0;
			if (fieldType == typeof(long)) return _inner.GetInt64(ordinal) != 0L;
			if (fieldType == typeof(decimal)) return _inner.GetDecimal(ordinal) != 0m;
			// Already a real boolean, or some other type — let the provider answer.
		}
		return _inner.GetBoolean(ordinal);
	}

	public override Guid GetGuid(int ordinal)
	{
		// Both real CHAR(16) OCTETS columns and BINARY(16) columns end up either
		// as byte[] on the wire or already as Guid. The provider's GetGuid handles
		// both cases for the FbDbType.Guid path; for the override path we may need
		// to construct it ourselves.
		if (GetColumnKind(ordinal) == DomainKind.Guid)
		{
			var fieldType = _inner.GetFieldType(ordinal);
			if (fieldType == typeof(byte[]))
				return new Guid(_inner.GetFieldValue<byte[]>(ordinal));
			if (fieldType == typeof(string))
				return new Guid(_inner.GetString(ordinal));
		}
		return _inner.GetGuid(ordinal);
	}

	public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);
	public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
	public override char GetChar(int ordinal) => _inner.GetChar(ordinal);
	public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length) => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
	public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);
	public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);
	public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);
	public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);
	public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);
	public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);
	public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);
	public override string GetString(int ordinal) => _inner.GetString(ordinal);

	public override DataTable GetSchemaTable()
	{
		var inner = _inner.GetSchemaTable();
		if (inner == null)
			return null;
		EnsureColumnKinds();
		if (_columnKinds == null)
			return inner;

		var dataTypeColumn = inner.Columns["DataType"];
		var providerTypeColumn = inner.Columns["ProviderType"];
		var dataTypeNameColumn = inner.Columns["DataTypeName"];

		for (var i = 0; i < inner.Rows.Count && i < _columnKinds.Length; i++)
		{
			var kind = _columnKinds[i];
			if (kind == DomainKind.None)
				continue;
			var row = inner.Rows[i];
			if (dataTypeColumn != null)
			{
				row[dataTypeColumn] = kind == DomainKind.Boolean ? typeof(bool) : typeof(Guid);
			}
			if (dataTypeNameColumn != null)
			{
				row[dataTypeNameColumn] = kind == DomainKind.Boolean ? "BOOLEAN" : "GUID";
			}
			// ProviderType holds the FbDbType enum value as int; we leave it alone
			// so callers that use it for low-level decisions still see the wire type.
			_ = providerTypeColumn;
		}
		return inner;
	}

	public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

	private DomainKind GetColumnKind(int ordinal)
	{
		EnsureColumnKinds();
		if (_columnKinds == null)
			return DomainKind.None;
		if (ordinal < 0 || ordinal >= _columnKinds.Length)
			return DomainKind.None;
		return _columnKinds[ordinal];
	}

	private void EnsureColumnKinds()
	{
		if (_columnKindsResolved)
			return;
		_columnKindsResolved = true;
		if (_connection == null || !_connection.CompiledOptions.HasAny)
			return;

		var schema = _inner.GetSchemaTable();
		if (schema == null)
			return;

		var fieldCount = _inner.FieldCount;
		var kinds = new DomainKind[fieldCount];
		var keys = new (string Relation, string Field)[fieldCount];

		var baseTableNameCol = schema.Columns["BaseTableName"];
		var baseColumnNameCol = schema.Columns["BaseColumnName"];
		if (baseTableNameCol == null || baseColumnNameCol == null)
			return;

		for (var i = 0; i < fieldCount && i < schema.Rows.Count; i++)
		{
			var row = schema.Rows[i];
			var rel = row[baseTableNameCol] as string;
			var fld = row[baseColumnNameCol] as string;
			if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(fld))
				continue;
			keys[i] = (rel, fld);
		}

		// Single batched lookup for all columns we haven't seen yet.
		_connection.SchemaCache.PrefetchMany(_connection.InnerConnection, keys);

		var compiled = _connection.CompiledOptions;
		var anyMatched = false;
		for (var i = 0; i < fieldCount; i++)
		{
			if (string.IsNullOrEmpty(keys[i].Relation) || string.IsNullOrEmpty(keys[i].Field))
				continue;
			var domain = _connection.SchemaCache.GetDomain(_connection.InnerConnection, keys[i].Relation, keys[i].Field);
			if (string.IsNullOrEmpty(domain))
				continue;
			var kind = compiled.Classify(domain);
			if (kind != DomainKind.None)
			{
				kinds[i] = kind;
				anyMatched = true;
			}
		}

		if (anyMatched)
			_columnKinds = kinds;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_inner?.Dispose();
		}
		base.Dispose(disposing);
	}

	public override async ValueTask DisposeAsync()
	{
		if (_inner != null)
		{
			await _inner.DisposeAsync().ConfigureAwait(false);
		}
		await base.DisposeAsync().ConfigureAwait(false);
	}
}
