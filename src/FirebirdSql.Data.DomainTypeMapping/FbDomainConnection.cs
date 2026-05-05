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
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace FirebirdSql.Data.DomainTypeMapping;

public sealed class FbDomainConnection : DbConnection, ICloneable
{
	private readonly FbConnection _inner;
	private readonly bool _ownsInner;
	private readonly DomainTypeMappingOptions _options;
	private readonly CompiledDomainOptions _compiled;
	private readonly DomainSchemaCache _schemaCache;

	public FbDomainConnection()
		: this(new FbConnection(), true, new DomainTypeMappingOptions())
	{ }

	public FbDomainConnection(string connectionString)
		: this(new FbConnection(connectionString), true, new DomainTypeMappingOptions())
	{ }

	public FbDomainConnection(string connectionString, DomainTypeMappingOptions options)
		: this(new FbConnection(connectionString), true, options)
	{ }

	public FbDomainConnection(FbConnection inner, DomainTypeMappingOptions options)
		: this(inner, false, options)
	{ }

	private FbDomainConnection(FbConnection inner, bool ownsInner, DomainTypeMappingOptions options)
	{
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
		_ownsInner = ownsInner;
		_options = (options ?? new DomainTypeMappingOptions()).Clone();
		_compiled = _options.Compile();
		_schemaCache = new DomainSchemaCache();
	}

	// The underlying FirebirdClient connection. Exposed for callers that need
	// to use provider-specific APIs (FbConnection.GetSchema, FbBackup, …).
	public FbConnection InnerConnection => _inner;

	internal CompiledDomainOptions CompiledOptions => _compiled;
	internal DomainSchemaCache SchemaCache => _schemaCache;

	public override string ConnectionString
	{
		get => _inner.ConnectionString;
		set => _inner.ConnectionString = value;
	}

	public override int ConnectionTimeout => _inner.ConnectionTimeout;
	public override string Database => _inner.Database;
	public override string DataSource => _inner.DataSource;
	public override string ServerVersion => _inner.ServerVersion;
	public override ConnectionState State => _inner.State;

	public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);

	public override void Open()
	{
		_inner.Open();
		_schemaCache.Clear();
	}

	public override Task OpenAsync(CancellationToken cancellationToken)
	{
		_schemaCache.Clear();
		return _inner.OpenAsync(cancellationToken);
	}

	public override void Close()
	{
		_schemaCache.Clear();
		_inner.Close();
	}

	protected override DbCommand CreateDbCommand()
	{
		return new FbDomainCommand(this);
	}

	public new FbDomainCommand CreateCommand()
	{
		return new FbDomainCommand(this);
	}

	protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
	{
		return new FbDomainTransaction(this, _inner.BeginTransaction(isolationLevel));
	}

	public new FbDomainTransaction BeginTransaction()
	{
		return new FbDomainTransaction(this, _inner.BeginTransaction());
	}

	public new FbDomainTransaction BeginTransaction(IsolationLevel level)
	{
		return new FbDomainTransaction(this, _inner.BeginTransaction(level));
	}

	public override DataTable GetSchema() => _inner.GetSchema();
	public override DataTable GetSchema(string collectionName) => _inner.GetSchema(collectionName);
	public override DataTable GetSchema(string collectionName, string[] restrictionValues) => _inner.GetSchema(collectionName, restrictionValues);

	public object Clone()
	{
		return new FbDomainConnection((FbConnection)((ICloneable)_inner).Clone(), true, _options);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_schemaCache.Clear();
			if (_ownsInner)
			{
				_inner.Dispose();
			}
		}
		base.Dispose(disposing);
	}

	public override async ValueTask DisposeAsync()
	{
		_schemaCache.Clear();
		if (_ownsInner)
		{
			await _inner.DisposeAsync().ConfigureAwait(false);
		}
		await base.DisposeAsync().ConfigureAwait(false);
	}
}
