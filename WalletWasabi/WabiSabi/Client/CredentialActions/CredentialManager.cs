using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Crypto.ZeroKnowledge;
using WalletWasabi.WabiSabi.Client.CredentialActions;
using WalletWasabi.WabiSabi.Client.CredentialDependencies;

namespace WalletWasabi.WabiSabi.Client.Reissuance
{
	public class CredentialManager
	{
		public CredentialManager(DependencyGraph g, List<AliceClient> aliceClients)
		{
			Graph = g;
			PendingCredentialsToPresent = Graph.Vertices.ToDictionary(v => v, _ => DependencyGraph.CredentialTypes.ToDictionary(t => t, _ => new List<Credential>()));

			var startingNodes = UnblockedRequests();

			foreach (var alice in aliceClients)
			{
				startingNodes.First(n => n.)
				alice.Coin.Amount;
			}
			aliceClients.First(a => a.Coin.Amount)

			PendingCredentialsToPresent
		}

		private DependencyGraph Graph { get; }
		private Dictionary<RequestNode, Dictionary<CredentialType, List<Credential>>> PendingCredentialsToPresent { get; }
		private List<(Task<ImmutableSortedDictionary<CredentialType, IEnumerable<Credential>>> Task, ImmutableSortedDictionary<CredentialType, IEnumerable<CredentialDependency>> Dependencies)> InFlightRequests { get; } = new();

		public async void DoItAsync(BobClient bobClient, CancellationToken cancellationToken)
		{
			for (var remainingSteps = 2 * PendingCredentialsToPresent.Count; remainingSteps > 0 && PendingCredentialsToPresent.Count + InFlightRequests.Count > 0; remainingSteps--)
			{
				// Clear unblocked but waiting requests. Not very efficient
				// (quadratic complexity), but good enough for demonstration
				// purposes.
				foreach (var node in UnblockedRequests())
				{
					// FIXME this is a little ugly, how should it look in the real code? seems like we're missing an abstraction
					var edgesByType = DependencyGraph.CredentialTypes.ToImmutableSortedDictionary(t => t, t => Graph.OutEdges(node, t));
					var credentialsToRequest = edgesByType.ToImmutableSortedDictionary(kvp => kvp.Key, kvp => kvp.Value.Select(e => e.Value));
					var credentialsToPresent = PendingCredentialsToPresent[node].ToImmutableSortedDictionary(kvp => kvp.Key, kvp => kvp.Value.AsEnumerable());

					var task = SimulateRequestAsync(bobClient, credentialsToPresent, credentialsToRequest, cancellationToken);

					InFlightRequests.Add((Task: task, Dependencies: edgesByType));
					PendingCredentialsToPresent.Remove(node);
				}

				// At this point at least one task must be in progress.
				if (InFlightRequests.Count == 0)
				{
					throw new InvalidOperationException();
				}

				// Wait for a response to arrive
				var i = Task.WaitAny(InFlightRequests.Select(x => x.Task).ToArray(), cancellationToken);

				var entry = InFlightRequests[i];
				InFlightRequests.RemoveAt(i);

				var issuedCredentials = await entry.Task.ConfigureAwait(false);

				// Unblock the requests that depend on the issued credentials from this response
				foreach ((var credentialType, var edges) in entry.Dependencies)
				{
					if (edges.Count() != issuedCredentials[credentialType].Count())
					{
						throw new InvalidOperationException();
					}

					foreach ((var credential, var edge) in issuedCredentials[credentialType].Zip(edges))
					{
						// Ignore the fact that credential is the same as
						// edge.Value, it's meant to represent the real thing
						// since it's returned from the task.
						if (edge.Value != credential)
						{
							throw new InvalidOperationException();
						}
						PendingCredentialsToPresent[edge.To][credentialType].Add(credential);
					}
				}
			}
		}

		private Dictionary<RequestNode, SmartRequestNode> SmartRequestNodes { get; } = new();

		private ImmutableArray<RequestNode> UnblockedRequests()
		{
			return PendingCredentialsToPresent.Keys.Where(node => DependencyGraph.CredentialTypes.All(t => Graph.InDegree(node, t) == PendingCredentialsToPresent[node][t].Count)).ToImmutableArray();
		}

		private async Task<ImmutableSortedDictionary<CredentialType, IEnumerable<Credential>>> SimulateRequestAsync(
			BobClient bobClient,
			ImmutableSortedDictionary<CredentialType, IEnumerable<Credential>> presented,
			ImmutableSortedDictionary<CredentialType, IEnumerable<long>> requested,
			CancellationToken cancellationToken)
		{
			var amountCreds = requested[CredentialType.Amount];
			var vsizeCreds = requested[CredentialType.Vsize];

			var result = await bobClient.ReissueCredentialsAsync(
				amountCreds.First(),
				amountCreds.Last(),
				vsizeCreds.First(),
				vsizeCreds.Last(),
				presented[CredentialType.Amount],
				presented[CredentialType.Vsize],
				cancellationToken).ConfigureAwait(false);

			var builder = ImmutableSortedDictionary.CreateBuilder<CredentialType, IEnumerable<Credential>>();
			builder.Add(CredentialType.Amount, result.RealAmountCredentials);
			builder.Add(CredentialType.Vsize, result.RealVsizeCredentials);
			return builder.ToImmutable();
		}
	}
}
