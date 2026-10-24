using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tbot.Services;

namespace Tbot.Models {
	sealed class TbotInstanceData : IEquatable<TbotInstanceData> {
		public ITBotMain _botMain;
		public IServiceScope ServiceScope;
		public string _botSettingsPath;
		public string _alias;
		public bool IsInitialized { get; set; } = true;
		private bool _deinitialized;
		private Exception _cleanupFailure;

		public TbotInstanceData(ITBotMain botMain, IServiceScope serviceScope, string botSettingsPath, string alias) {
			_botMain = botMain;
			_botSettingsPath = botSettingsPath;
			_alias = alias;
			ServiceScope = serviceScope;
		}

		public bool Equals(TbotInstanceData other) {
			// Equality must check of settings path is the same, since user may change Alias and settings internal data
			if (other == null)
				return false;

			return (_botSettingsPath == other._botSettingsPath);
		}

		public async Task Deinitialize() {
			if (_deinitialized)
				return;
			if (_cleanupFailure != null)
				throw new InvalidOperationException($"Cleanup previously failed for instance \"{_alias}\"; a replacement cannot be started safely.", _cleanupFailure);

			IsInitialized = false;
			try {
				// The DI scope owns its async services, including TBotMain.
				if (ServiceScope is IAsyncDisposable asyncScope) {
					await asyncScope.DisposeAsync();
				} else {
					try {
						if (_botMain != null)
			await _botMain.DisposeAsync();
					} finally {
						ServiceScope?.Dispose();
					}
				}
				_deinitialized = true;
			} catch (Exception exception) {
				// A failed DI disposal cannot reliably be retried: the scope may
				// already consider itself disposed while a service is still alive.
				_cleanupFailure = exception;
				throw;
		}
	}
}
}
