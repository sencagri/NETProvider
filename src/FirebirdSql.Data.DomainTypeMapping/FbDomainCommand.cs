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
using FirebirdSql.Data.DomainTypeMapping.Internal;
using FirebirdSql.Data.FirebirdClient;

namespace FirebirdSql.Data.DomainTypeMapping;

public sealed class FbDomainCommand : DbCommand
{
	private readonly FbCommand _inner;
	private FbDomainConnection _connection;
	private FbDomainTransaction _transaction;

	internal FbDomainCommand(FbDomainConnection connection)
	{
		_connection = connection;
		_inner = new FbCommand
		{
			Connection = connection.InnerConnection,
		};
	}

	public FbCommand InnerCommand => _inner;

	public override string CommandText
	{
		get => _inner.CommandText;
		set => _inner.CommandText = value;
	}

	public override int CommandTimeout
	{
		get => _inner.CommandTimeout;
		set => _inner.CommandTimeout = value;
	}

	public override CommandType CommandType
	{
		get => _inner.CommandType;
		set => _inner.CommandType = value;
	}

	public override UpdateRowSource UpdatedRowSource
	{
		get => _inner.UpdatedRowSource;
		set => _inner.UpdatedRowSource = value;
	}

	public override bool DesignTimeVisible
	{
		get => _inner.DesignTimeVisible;
		set => _inner.DesignTimeVisible = value;
	}

	protected override DbConnection DbConnection
	{
		get => _connection;
		set
		{
			if (value is FbDomainConnection fbDomain)
			{
				_connection = fbDomain;
				_inner.Connection = fbDomain.InnerConnection;
			}
			else if (value is FbConnection fb)
			{
				// Caller bypassed the wrapper; we can still attach but no domain mapping.
				_connection = null;
				_inner.Connection = fb;
			}
			else if (value == null)
			{
				_connection = null;
				_inner.Connection = null;
			}
			else
			{
				throw new ArgumentException(
					$"Connection must be {nameof(FbDomainConnection)} or {nameof(FbConnection)}.", nameof(value));
			}
		}
	}

	protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

	protected override DbTransaction DbTransaction
	{
		get => _transaction;
		set
		{
			if (value is FbDomainTransaction wrapped)
			{
				_transaction = wrapped;
				_inner.Transaction = wrapped.InnerTransaction;
			}
			else if (value is FbTransaction fbTx)
			{
				_transaction = null;
				_inner.Transaction = fbTx;
			}
			else if (value == null)
			{
				_transaction = null;
				_inner.Transaction = null;
			}
			else
			{
				throw new ArgumentException(
					$"Transaction must be {nameof(FbDomainTransaction)} or {nameof(FbTransaction)}.", nameof(value));
			}
		}
	}

	public override void Cancel() => _inner.Cancel();
	public override void Prepare() => _inner.Prepare();
	public override Task PrepareAsync(CancellationToken cancellationToken = default) => _inner.PrepareAsync(cancellationToken);

	protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

	public override int ExecuteNonQuery()
	{
		NormaliseParameters();
		return _inner.ExecuteNonQuery();
	}

	public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
	{
		NormaliseParameters();
		return await _inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public override object ExecuteScalar()
	{
		NormaliseParameters();
		return _inner.ExecuteScalar();
	}

	public override async Task<object> ExecuteScalarAsync(CancellationToken cancellationToken)
	{
		NormaliseParameters();
		return await _inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
	}

	protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
	{
		NormaliseParameters();
		var reader = _inner.ExecuteReader(behavior);
		return WrapReader(reader);
	}

	protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
	{
		NormaliseParameters();
		var reader = await _inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
		return WrapReader(reader);
	}

	private DbDataReader WrapReader(DbDataReader inner)
	{
		if (_connection == null || !_connection.CompiledOptions.HasAny)
			return inner;
		return new FbDomainDataReader(inner, _connection);
	}

	// Walks the parameter collection and converts bool / Guid values whose
	// destination is a non-native wire type (SMALLINT-as-bool, CHAR(16)-as-Guid).
	// We Prepare the inner command so the wire descriptor is populated, then
	// reflect into it to learn each parameter's actual SqlType.
	private void NormaliseParameters()
	{
		var parameters = _inner.Parameters;
		if (parameters.Count == 0)
			return;
		if (_connection == null || !_connection.CompiledOptions.HasAny)
			return;

		// Cheap pre-check: skip the Prepare round-trip unless we actually have a
		// value that *might* need conversion.
		var needsWork = false;
		for (var i = 0; i < parameters.Count; i++)
		{
			var v = ((FbParameter)parameters[i]).Value;
			if (v is bool || v is Guid)
			{
				needsWork = true;
				break;
			}
		}
		if (!needsWork)
			return;

		_inner.Prepare();
		var sqlTypes = StatementReflection.GetParameterSqlTypes(_inner);
		if (sqlTypes == null || sqlTypes.Length != parameters.Count)
			return;

		for (var i = 0; i < parameters.Count; i++)
		{
			var p = (FbParameter)parameters[i];
			var converted = ConvertIfNeeded(p.Value, sqlTypes[i]);
			if (!ReferenceEquals(converted, p.Value))
			{
				p.Value = converted;
			}
		}
	}

	// Returns a possibly-substituted value: bool boxed as the matching numeric
	// type when the wire expects an integer; original value otherwise. Setting
	// FbParameter.Value = the new value lets the provider's existing fill loop
	// serialise it correctly. We do not touch FbDbType — the descriptor's
	// type comes from the server, not from the parameter.
	private static object ConvertIfNeeded(object value, int sqlType)
	{
		var normalised = FirebirdSqlTypeCodes.Normalize(sqlType);

		if (value is bool b)
		{
			switch (normalised)
			{
				case FirebirdSqlTypeCodes.SQL_SHORT:
					return (short)(b ? 1 : 0);
				case FirebirdSqlTypeCodes.SQL_LONG:
					return b ? 1 : 0;
				case FirebirdSqlTypeCodes.SQL_INT64:
					return b ? 1L : 0L;
				default:
					return value;
			}
		}

		// Guid values are already handled by the provider's CHAR(16) OCTETS
		// path when the user typed FbDbType.Guid. We don't intercept here.
		return value;
	}
}
