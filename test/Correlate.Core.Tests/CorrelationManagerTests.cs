﻿using System;
using System.Collections;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Correlate
{
	public class CorrelationManagerTests
	{
		private readonly CorrelationContextAccessor _correlationContextAccessor;
		private readonly CorrelationManager _sut;
		private readonly Mock<ICorrelationIdFactory> _correlationIdFactoryMock;
		private const string GeneratedCorrelationId = "generated-correlation-id";

		public CorrelationManagerTests()
		{
			_correlationContextAccessor = new CorrelationContextAccessor();

			_correlationIdFactoryMock = new Mock<ICorrelationIdFactory>();
			_correlationIdFactoryMock
				.Setup(m => m.Create())
				.Returns(() => GeneratedCorrelationId)
				.Verifiable();

			_sut = new CorrelationManager(
				new CorrelationContextFactory(_correlationContextAccessor),
				_correlationIdFactoryMock.Object,
				_correlationContextAccessor,
				new NullLogger<CorrelationManager>()
			);
		}

		public class Async : CorrelationManagerTests
		{
			[Fact]
			public async Task Given_a_task_should_run_task_inside_correlated_context()
			{
				// Pre-assert
				_correlationContextAccessor.CorrelationContext.Should().BeNull();

				// Act
				await _sut.CorrelateAsync(() =>
				{
					// Inline assert
					_correlationContextAccessor.CorrelationContext.Should().NotBeNull();
					return Task.CompletedTask;
				});

				// Post-assert
				_correlationContextAccessor.CorrelationContext.Should().BeNull();
			}

			[Fact]
			public async Task When_running_correlated_task_without_correlation_id_should_use_generate_one()
			{
				// Act
				await _sut.CorrelateAsync(() =>
				{
					// Inline assert
					_correlationContextAccessor.CorrelationContext.CorrelationId.Should().Be(GeneratedCorrelationId);
					return Task.CompletedTask;
				});

				_correlationIdFactoryMock.Verify();
			}

			[Fact]
			public async Task When_running_correlated_task_with_correlation_id_should_use_it()
			{
				const string correlationId = "my-correlation-id";

				// Act
				await _sut.CorrelateAsync(correlationId,
					() =>
					{
						// Inline assert
						_correlationContextAccessor.CorrelationContext.CorrelationId.Should().Be(correlationId);
						return Task.CompletedTask;
					});

				_correlationIdFactoryMock.Verify(m => m.Create(), Times.Never);
			}

			[Fact]
			public void When_not_providing_task_when_starting_correlation_should_throw()
			{
				Func<Task> correlatedTask = null;

				// Act
				// ReSharper disable once ExpressionIsAlwaysNull
				Func<Task> act = () => _sut.CorrelateAsync(null, correlatedTask, null);

				// Assert
				act.Should()
					.Throw<ArgumentNullException>()
					.Which.ParamName.Should()
					.Be(nameof(correlatedTask));
			}

			[Fact]
			public void When_provided_task_throws_should_not_wrap_exception()
			{
				var exception = new Exception();
				async Task ThrowingTask()
				{
					await Task.Yield();
					throw exception;
				}

				// Act
				Func<Task> act = () => _sut.CorrelateAsync(null, ThrowingTask);

				// Assert
				act.Should().Throw<Exception>().Which.Should().Be(exception);
			}

			[Fact]
			public void When_provided_task_throws_should_enrich_exception_with_correlationId()
			{
				var exception = new Exception();
				Task ThrowingTask() => throw exception;

				// Act
				Func<Task> act = () => _sut.CorrelateAsync(null, ThrowingTask);

				// Assert
				IDictionary exceptionData = act.Should().Throw<Exception>().Which.Data;
				exceptionData.Keys.Should().Contain(CorrelateConstants.CorrelationIdKey);
				exceptionData[CorrelateConstants.CorrelationIdKey].Should().Be(GeneratedCorrelationId);
			}

			[Fact]
			public void When_handling_exception_with_delegate_should_not_throw()
			{
				var exception = new Exception();
				const bool handlesException = true;

				// Act
				Func<Task> act = () => _sut.CorrelateAsync(
					null,
					() => throw exception,
					ctx =>
					{
						ctx.CorrelationContext.CorrelationId.Should().Be(GeneratedCorrelationId);
						ctx.Exception.Should().Be(exception);
						ctx.IsExceptionHandled = handlesException;
					});

				// Assert
				act.Should().NotThrow();
			}

			[Fact]
			public async Task When_handling_exception_by_returning_new_value_should_not_throw()
			{
				var exception = new Exception();
				async Task<int> ThrowingTask()
				{
					await Task.Yield();
					throw exception;
				}
				const int returnValue = 12345;

				// Act
				Func<Task<int>> act = () => _sut.CorrelateAsync(
					null,
					ThrowingTask,
					ctx =>
					{
						ctx.CorrelationContext.CorrelationId.Should().Be(GeneratedCorrelationId);
						ctx.Exception.Should().Be(exception);
						ctx.Result = returnValue;
					});

				// Assert
				act.Should().NotThrow();
				(await act()).Should().Be(returnValue);
			}

			[Fact]
			public void When_not_handling_exception_with_delegate_should_still_throw()
			{
				var exception = new Exception();
				const bool handlesException = false;

				// Act
				Func<Task> act = () => _sut.CorrelateAsync(
					() => throw exception,
					ctx => ctx.IsExceptionHandled = handlesException
				);

				// Assert
				act.Should().Throw<Exception>().Which.Should().Be(exception);
			}

			[Fact]
			public Task When_starting_correlationContext_when_another_context_is_active_should_start_new()
			{
				const string parentContextId = nameof(parentContextId);
				const string innerContextId = nameof(innerContextId);

				return _sut.CorrelateAsync(parentContextId, 
					async () =>
					{
						CorrelationContext parentContext = _correlationContextAccessor.CorrelationContext;
						parentContext.Should().NotBeNull();
						parentContext.CorrelationId.Should().Be(parentContextId);

						await _sut.CorrelateAsync(innerContextId,
							() =>
							{
								CorrelationContext innerContext = _correlationContextAccessor.CorrelationContext;
								innerContext.Should().NotBeNull();
								innerContext.Should().NotBe(parentContext);
								innerContext.CorrelationId.Should().Be(innerContextId);

								return Task.CompletedTask;
							});

						_correlationContextAccessor.CorrelationContext.Should().NotBeNull();

						_correlationContextAccessor.CorrelationContext
							.CorrelationId
							.Should()
							.Be(parentContextId);
					});
			}

			[Fact]
			public Task When_starting_correlationContext_inside_running_context_with_same_id_should_reuse()
			{
				return _sut.CorrelateAsync(async () =>
				{
					CorrelationContext parentContext = _correlationContextAccessor.CorrelationContext;

					await _sut.CorrelateAsync(parentContext.CorrelationId,
						() =>
						{
							CorrelationContext innerContext = _correlationContextAccessor.CorrelationContext;
							innerContext.Should()
								.NotBe(parentContext)
								.And.BeEquivalentTo(parentContext);

							return Task.CompletedTask;
						});
				});
			}

			[Fact]
			public Task When_starting_correlationContext_inside_running_context_without_specifying_should_reuse()
			{
				return _sut.CorrelateAsync(async () =>
				{
					CorrelationContext parentContext = _correlationContextAccessor.CorrelationContext;

					await _sut.CorrelateAsync(() =>
					{
						CorrelationContext innerContext = _correlationContextAccessor.CorrelationContext;
						innerContext.Should()
							.NotBe(parentContext)
							.And.BeEquivalentTo(parentContext);

						return Task.CompletedTask;
					});
				});
			}

			[Fact]
			public Task When_starting_correlationContext_with_legacy_ctor_when_another_context_is_active_should_not_throw()
			{
				const string parentContextId = nameof(parentContextId);

#pragma warning disable 618 // justification, covering legacy implementation (pre v3.0)
				var sut = new CorrelationManager(
					new CorrelationContextFactory(_correlationContextAccessor),
					_correlationIdFactoryMock.Object,
					new NullLogger<CorrelationManager>()
				);
#pragma warning restore 618

				return sut.CorrelateAsync(parentContextId,
					async () =>
					{
						CorrelationContext parentContext = _correlationContextAccessor.CorrelationContext;
						parentContext.Should().NotBeNull();
						parentContext.CorrelationId.Should().Be(parentContextId);

						await sut.CorrelateAsync(() =>
						{
							CorrelationContext innerContext = _correlationContextAccessor.CorrelationContext;
							innerContext.Should().NotBeNull().And.NotBe(parentContext);
							innerContext.CorrelationId.Should().NotBe(parentContextId);

							return Task.CompletedTask;
						});
					});
			}

			[Fact]
			public async Task Given_task_returns_a_value_when_executed_should_return_value()
			{
				const int value = 12345;

				// Pre-assert
				_correlationContextAccessor.CorrelationContext.Should().BeNull();

				// Act
				int actual = await _sut.CorrelateAsync(() =>
				{
					// Inline assert
					_correlationContextAccessor.CorrelationContext.Should().NotBeNull();
					return Task.FromResult(value);
				});

				// Post-assert
				actual.Should().Be(value);
				_correlationContextAccessor.CorrelationContext.Should().BeNull();
			}
		}

		public class Sync : CorrelationManagerTests
		{
			[Fact]
			public void Given_a_action_should_run_action_inside_correlated_context()
			{
				// Pre-assert
				_correlationContextAccessor.CorrelationContext.Should().BeNull();

				// Act
				_sut.Correlate(() =>
				{
					// Inline assert
					_correlationContextAccessor.CorrelationContext.Should().NotBeNull();
				});

				// Post-assert
				_correlationContextAccessor.CorrelationContext.Should().BeNull();
			}

			[Fact]
			public void When_running_correlated_action_without_correlation_id_should_use_generate_one()
			{
				// Act
				_sut.Correlate(() =>
				{
					// Inline assert
					_correlationContextAccessor.CorrelationContext.CorrelationId.Should().Be(GeneratedCorrelationId);
				});

				_correlationIdFactoryMock.Verify();
			}

			[Fact]
			public void When_running_correlated_action_with_correlation_id_should_use_it()
			{
				const string correlationId = "my-correlation-id";

				// Act
				_sut.Correlate(correlationId,
					() =>
					{
						// Inline assert
						_correlationContextAccessor.CorrelationContext.CorrelationId.Should().Be(correlationId);
					});

				_correlationIdFactoryMock.Verify(m => m.Create(), Times.Never);
			}

			[Fact]
			public void When_not_providing_action_when_starting_correlation_should_throw()
			{
				Action correlatedAction = null;

				// Act
				// ReSharper disable once ExpressionIsAlwaysNull
				Action act = () => _sut.Correlate(null, correlatedAction, null);

				// Assert
				act.Should()
					.Throw<ArgumentNullException>()
					.Which.ParamName.Should()
					.Be(nameof(correlatedAction));
			}

			[Fact]
			public void When_provided_action_throws_should_not_wrap_exception()
			{
				var exception = new Exception();

				void ThrowingAction() => throw exception;

				// Act
				Action act = () => _sut.Correlate(null, ThrowingAction);

				// Assert
				act.Should().Throw<Exception>().Which.Should().Be(exception);
			}

			[Fact]
			public void When_provided_action_throws_should_enrich_exception_with_correlationId()
			{
				var exception = new Exception();
				void ThrowingAction() => throw exception;

				// Act
				Action act = () => _sut.Correlate(null, ThrowingAction);

				// Assert
				IDictionary exceptionData = act.Should().Throw<Exception>().Which.Data;
				exceptionData.Keys.Should().Contain(CorrelateConstants.CorrelationIdKey);
				exceptionData[CorrelateConstants.CorrelationIdKey].Should().Be(GeneratedCorrelationId);
			}

			[Fact]
			public void When_handling_exception_with_delegate_should_not_throw()
			{
				var exception = new Exception();
				const bool handlesException = true;

				// Act
				Action act = () => _sut.Correlate(
					null,
					() => throw exception,
					ctx =>
					{
						ctx.CorrelationContext.CorrelationId.Should().Be(GeneratedCorrelationId);
						ctx.Exception.Should().Be(exception);
						ctx.IsExceptionHandled = handlesException;
					});

				// Assert
				act.Should().NotThrow();
			}

			[Fact]
			public void When_handling_exception_by_returning_new_value_should_not_throw()
			{
				var exception = new Exception();
				int ThrowingFunc()
				{
					throw exception;
				}
				const int returnValue = 12345;

				// Act
				Func<int> act = () => _sut.Correlate(
					null,
					ThrowingFunc,
					ctx =>
					{
						ctx.CorrelationContext.CorrelationId.Should().Be(GeneratedCorrelationId);
						ctx.Exception.Should().Be(exception);
						ctx.Result = returnValue;
					});

				// Assert
				act.Should().NotThrow();
				act().Should().Be(returnValue);
			}

			[Fact]
			public void When_not_handling_exception_with_delegate_should_still_throw()
			{
				var exception = new Exception();
				const bool handlesException = false;

				// Act
				Action act = () => _sut.Correlate(
					() => throw exception,
					ctx => ctx.IsExceptionHandled = handlesException
				);

				// Assert
				act.Should().Throw<Exception>().Which.Should().Be(exception);
			}

			[Fact]
			public void When_starting_correlationContext_when_another_context_is_active_should_start_new()
			{
				const string parentContextId = nameof(parentContextId);
				const string innerContextId = nameof(innerContextId);

				_sut.Correlate(parentContextId,
					() =>
					{
						CorrelationContext parentContext = _correlationContextAccessor.CorrelationContext;
						parentContext.Should().NotBeNull();
						parentContext.CorrelationId.Should().Be(parentContextId);

						_sut.Correlate(innerContextId,
							() =>
							{
								CorrelationContext innerContext = _correlationContextAccessor.CorrelationContext;
								innerContext.Should().NotBeNull();
								innerContext.Should().NotBe(parentContext);
								innerContext.CorrelationId.Should().Be(innerContextId);
							});

						_correlationContextAccessor.CorrelationContext.Should().NotBeNull();

						_correlationContextAccessor.CorrelationContext
							.CorrelationId
							.Should()
							.Be(parentContextId);
					});
			}

			[Fact]
			public void When_starting_correlationContext_inside_running_context_with_same_id_should_reuse()
			{
				_sut.Correlate(() =>
				{
					CorrelationContext parentContext = _correlationContextAccessor.CorrelationContext;

					_sut.Correlate(parentContext.CorrelationId,
						() =>
						{
							CorrelationContext innerContext = _correlationContextAccessor.CorrelationContext;
							innerContext.Should()
								.NotBe(parentContext)
								.And.BeEquivalentTo(parentContext);
						});
				});
			}

			[Fact]
			public void When_starting_correlationContext_inside_running_context_without_specifying_should_reuse()
			{
				_sut.Correlate(() =>
				{
					CorrelationContext parentContext = _correlationContextAccessor.CorrelationContext;

					_sut.Correlate(() =>
					{
						CorrelationContext innerContext = _correlationContextAccessor.CorrelationContext;
						innerContext.Should()
							.NotBe(parentContext)
							.And.BeEquivalentTo(parentContext);
					});
				});
			}

			[Fact]
			public void Given_func_returns_a_value_when_executed_should_return_value()
			{
				const int value = 12345;

				// Pre-assert
				_correlationContextAccessor.CorrelationContext.Should().BeNull();

				// Act
				int actual = _sut.Correlate(() =>
				{
					// Inline assert
					_correlationContextAccessor.CorrelationContext.Should().NotBeNull();
					return value;
				});

				// Post-assert
				actual.Should().Be(value);
				_correlationContextAccessor.CorrelationContext.Should().BeNull();
			}
		}
	}
}