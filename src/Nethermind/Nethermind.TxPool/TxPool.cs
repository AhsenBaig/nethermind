//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Timers;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool.Collections;
using Timer = System.Timers.Timer;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.TxPool
{
    /// <summary>
    /// Stores all pending transactions. These will be used by block producer if this node is a miner / validator
    /// or simply for broadcasting and tracing in other cases.
    /// </summary>
    public class TxPool : ITxPool, IDisposable
    {
        public static IComparer<Transaction> DefaultComparer { get; } = 
            CompareTxByGasPrice.Instance
                .ThenBy(CompareTxByTimestamp.Instance)
                .ThenBy(CompareTxByPoolIndex.Instance)
                .ThenBy(CompareTxByGasLimit.Instance);
        
        private readonly object _locker = new object();

        private readonly ConcurrentDictionary<Address, AddressNonces> _nonces = new ConcurrentDictionary<Address, AddressNonces>();

        private readonly LruKeyCache<Keccak> _hashCache = new LruKeyCache<Keccak>(MemoryAllowance.TxHashCacheSize, MemoryAllowance.TxHashCacheSize, "tx hashes");

        /// <summary>
        /// Number of blocks after which own transaction will not be resurrected any more
        /// </summary>
        private const long FadingTimeInBlocks = 64;

        /// <summary>
        /// Notification threshold randomizer seed
        /// </summary>
        private static int _seed = Environment.TickCount;

        /// <summary>
        /// Random number generator for peer notification threshold - no need to be securely random.
        /// </summary>
        private static readonly ThreadLocal<Random> Random =
            new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref _seed)));

        private readonly SortedPool<Keccak, Transaction, Address> _transactions;

        private readonly ISpecProvider _specProvider;
        private readonly IReadOnlyStateProvider _stateProvider;
        private readonly IEthereumEcdsa _ecdsa;
        protected readonly ILogger _logger;

        /// <summary>
        /// Transactions published locally (initiated by this node users).
        /// </summary>
        private readonly ConcurrentDictionary<Keccak, Transaction> _ownTransactions = new ConcurrentDictionary<Keccak, Transaction>();

        /// <summary>
        /// Own transactions that were already added to the chain but need more confirmations
        /// before being removed from pending entirely.
        /// </summary>
        private readonly ConcurrentDictionary<Keccak, (Transaction tx, long blockNumber)> _fadingOwnTransactions
            = new ConcurrentDictionary<Keccak, (Transaction tx, long blockNumber)>();
        
        /// <summary>
        /// Long term storage for pending transactions.
        /// </summary>
        private readonly ITxStorage _txStorage;

        /// <summary>
        /// Connected peers that can be notified about transactions.
        /// </summary>
        private readonly ConcurrentDictionary<PublicKey, ITxPoolPeer> _peers = new ConcurrentDictionary<PublicKey, ITxPoolPeer>();

        /// <summary>
        /// Timer for rebroadcasting pending own transactions.
        /// </summary>
        private readonly Timer _ownTimer;

        /// <summary>
        /// Defines the percentage of peers that will be notified about pending transactions on average.
        /// </summary>
        private readonly int _peerNotificationThreshold;

        /// <summary>
        /// Indexes transactions
        /// </summary>
        private ulong _txIndex;

        /// <summary>
        /// This class stores all known pending transactions that can be used for block production
        /// (by miners or validators) or simply informing other nodes about known pending transactions (broadcasting).
        /// </summary>
        /// <param name="txStorage">Tx storage used to reject known transactions.</param>
        /// <param name="ecdsa">Used to recover sender addresses from transaction signatures.</param>
        /// <param name="specProvider">Used for retrieving information on EIPs that may affect tx signature scheme.</param>
        /// <param name="txPoolConfig"></param>
        /// <param name="stateProvider"></param>
        /// <param name="logManager"></param>
        /// <param name="comparer"></param>
        public TxPool(ITxStorage txStorage,
            IEthereumEcdsa ecdsa,
            ISpecProvider specProvider,
            ITxPoolConfig txPoolConfig,
            IReadOnlyStateProvider stateProvider,
            ILogManager logManager,
            IComparer<Transaction> comparer = null)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txStorage = txStorage ?? throw new ArgumentNullException(nameof(txStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));

            MemoryAllowance.MemPoolSize = txPoolConfig.Size;
            ThisNodeInfo.AddInfo("Mem est tx   :", $"{(LruCache<Keccak, object>.CalculateMemorySize(32, MemoryAllowance.TxHashCacheSize) + LruCache<Keccak, Transaction>.CalculateMemorySize(4096, MemoryAllowance.MemPoolSize)) / 1000 / 1000}MB".PadLeft(8));

            _transactions = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer ?? DefaultComparer);
            
            _peerNotificationThreshold = txPoolConfig.PeerNotificationThreshold;

            _ownTimer = new Timer(500);
            _ownTimer.Elapsed += OwnTimerOnElapsed;
            _ownTimer.AutoReset = false;
            _ownTimer.Start();
        }

        public Transaction[] GetPendingTransactions() => _transactions.GetSnapshot();
        
        public IDictionary<Address, Transaction[]> GetPendingTransactionsBySender() => _transactions.GetBucketSnapshot();

        public Transaction[] GetOwnPendingTransactions() => _ownTransactions.Values.ToArray();

        public void AddPeer(ITxPoolPeer peer)
        {
            if (!_peers.TryAdd(peer.Id, peer))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Added a peer to TX pool: {peer.Enode}");
        }

        public void RemovePeer(PublicKey nodeId)
        {
            if (!_peers.TryRemove(nodeId, out _))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Removed a peer from TX pool: {nodeId}");
        }

        public AddTxResult AddTransaction(Transaction tx, TxHandlingOptions handlingOptions)
        {
            if (tx.Hash == null)
            {
                throw new ArgumentException($"{nameof(tx.Hash)} not set on {nameof(Transaction)}");
            }
            
            NewDiscovered?.Invoke(this, new TxEventArgs(tx));
            
            bool managedNonce = (handlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
            bool isPersistentBroadcast = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == TxHandlingOptions.PersistentBroadcast;
            if (_logger.IsTrace) _logger.Trace($"Adding transaction {tx.ToString("  ")} - managed nonce: {managedNonce} | persistent broadcast {isPersistentBroadcast}");

            return FilterTransaction(tx, managedNonce) ?? AddCore(tx, isPersistentBroadcast);
        }

        private AddTxResult AddCore(Transaction tx, bool isPersistentBroadcast)
        {
            // !!! do not change it to |=
            bool isKnown = _hashCache.Get(tx.Hash);

            /*
             * we need to make sure that the sender is resolved before adding to the distinct tx pool
             * as the address is used in the distinct value calculation
             */
            if (!isKnown)
            {
                isKnown |= !_transactions.TryInsert(tx.Hash, tx);
            }

            if (!isKnown)
            {
                isKnown |= _txStorage.Get(tx.Hash) != null;
            }

            if (isKnown)
            {
                // If transaction is a bit older and already known then it may be stored in the persistent storage.
                Metrics.PendingTransactionsKnown++;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, already known.");
                return AddTxResult.AlreadyKnown;
            }

            tx.PoolIndex = _txIndex++;
            _hashCache.Set(tx.Hash);

            HandleOwnTransaction(tx, isPersistentBroadcast);

            NotifySelectedPeers(tx);
            StoreTx(tx);
            NewPending?.Invoke(this, new TxEventArgs(tx));
            return AddTxResult.Added;
        }

        protected virtual AddTxResult? FilterTransaction(Transaction tx, in bool managedNonce)
        {
            if (_fadingOwnTransactions.ContainsKey(tx.Hash))
            {
                _fadingOwnTransactions.TryRemove(tx.Hash, out (Transaction Tx, long _) fadingTxHolder);
                _ownTransactions.TryAdd(fadingTxHolder.Tx.Hash, fadingTxHolder.Tx);
                _ownTimer.Enabled = true;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, already known.");
                return AddTxResult.Added;
            }

            Metrics.PendingTransactionsReceived++;

//            if (tx.Signature.ChainId == null)
//            {
//                // Note that we are discarding here any transactions that follow the old signature scheme (no ChainId).
//                Metrics.PendingTransactionsDiscarded++;
//                return AddTxResult.OldScheme;
//            }

            if (tx.Signature.ChainId != null && tx.Signature.ChainId != _specProvider.ChainId)
            {
                // It may happen that other nodes send us transactions that were signed for another chain.
                Metrics.PendingTransactionsDiscarded++;
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, wrong chain.");
                return AddTxResult.InvalidChainId;
            }

            /* Note that here we should also test incoming transactions for old nonce.
             * This is not a critical check and it is expensive since it requires state read so it is better
             * if we leave it for block production only.
             * */

            if (managedNonce && CheckOwnTransactionAlreadyUsed(tx))
            {
                if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, nonce already used.");
                return AddTxResult.OwnNonceAlreadyUsed;
            }

            /* We have encountered multiple transactions that do not resolve sender address properly.
             * We need to investigate what these txs are and why the sender address is resolved to null.
             * Then we need to decide whether we really want to broadcast them.
             */
            if (tx.SenderAddress == null)
            {
                tx.SenderAddress = _ecdsa.RecoverAddress(tx);
                if (tx.SenderAddress == null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, no sender.");
                    return AddTxResult.PotentiallyUseless;
                }
            }

            return null;
        }

        private void HandleOwnTransaction(Transaction tx, bool isOwn)
        {
            if (isOwn)
            {
                _ownTransactions.TryAdd(tx.Hash, tx);
                _ownTimer.Enabled = true;
                if (_logger.IsDebug) _logger.Debug($"Broadcasting own transaction {tx.Hash} to {_peers.Count} peers");
                if(_logger.IsTrace) _logger.Trace($"Broadcasting transaction {tx.ToString("  ")}");
            }
        }

        private bool CheckOwnTransactionAlreadyUsed(Transaction transaction)
        {
            Address address = transaction.SenderAddress;
            lock (_locker)
            {
                if (!_nonces.TryGetValue(address, out var addressNonces))
                {
                    var currentNonce = _stateProvider.GetNonce(address);
                    addressNonces = new AddressNonces(currentNonce);
                    _nonces.TryAdd(address, addressNonces);
                }

                if (!addressNonces.Nonces.TryGetValue(transaction.Nonce, out var nonce))
                {
                    nonce = new Nonce(transaction.Nonce);
                    addressNonces.Nonces.TryAdd(transaction.Nonce, new Nonce(transaction.Nonce));
                }

                if (!(nonce.TransactionHash is null && nonce.TransactionHash != transaction.Hash))
                {
                    // Nonce conflict
                    if (_logger.IsDebug) _logger.Debug($"Nonce: {nonce.Value} was already used in transaction: '{nonce.TransactionHash}' and cannot be reused by transaction: '{transaction.Hash}'.");

                    return true;
                }

                nonce.SetTransactionHash(transaction.Hash);
            }

            return false;
        }

        public void RemoveTransaction(Keccak hash, long blockNumber, bool removeBelowThisTxNonce = false)
        {
            if (_fadingOwnTransactions.Count > 0)
            {
                /* If we receive a remove transaction call then it means that a block was processed (assumed).
                 * If our fading transaction has been included in the main chain more than FadingTimeInBlocks blocks ago
                 * then we can assume that is is set in stone (or rather blockchain) and we do not have to worry about
                 * it any more.
                 */
                foreach ((Keccak fadingHash, (Transaction Tx, long BlockNumber) fadingHolder) in _fadingOwnTransactions)
                {
                    if (fadingHolder.BlockNumber >= blockNumber - FadingTimeInBlocks)
                    {
                        continue;
                    }

                    _fadingOwnTransactions.TryRemove(fadingHash, out _);

                    // Nonce was correct and will never be used again
                    lock (_locker)
                    {
                        var address = fadingHolder.Tx.SenderAddress;
                        if (!_nonces.TryGetValue(address, out AddressNonces addressNonces))
                        {
                            continue;
                        }

                        addressNonces.Nonces.TryRemove(fadingHolder.Tx.Nonce, out _);
                        if (addressNonces.Nonces.IsEmpty)
                        {
                            _nonces.TryRemove(address, out _);
                        }
                    }
                }
            }
            
            if (_transactions.TryRemove(hash, out var transaction, out ICollection<Transaction> bucket))
            {
                RemovedPending?.Invoke(this, new TxEventArgs(transaction));
            }

            if (_ownTransactions.Count != 0)
            {
                bool ownIncluded = _ownTransactions.TryRemove(hash, out Transaction fadingTx);
                if (ownIncluded)
                {
                    _fadingOwnTransactions.TryAdd(hash, (fadingTx, blockNumber));
                    if (_logger.IsInfo) _logger.Trace($"Transaction {hash} created on this node was included in the block");
                }
            }

            _txStorage.Delete(hash);
            if (_logger.IsTrace) _logger.Trace($"Deleted a transaction: {hash}");

            if (bucket != null && removeBelowThisTxNonce)
            {
                Transaction txWithSmallestNonce = bucket.FirstOrDefault();
                while (txWithSmallestNonce != null && txWithSmallestNonce.Nonce <= transaction.Nonce)
                {
                    RemoveTransaction(txWithSmallestNonce.Hash, blockNumber);
                    txWithSmallestNonce = bucket.FirstOrDefault();
                }
            }
        }

        public bool TryGetPendingTransaction(Keccak hash, out Transaction transaction)
        {
            if (!_transactions.TryGetValue(hash, out transaction))
            {
                // commented out as it puts too much pressure on the database
                // and it not really required in any scenario
                  // * tx recovery usually will fetch from pending
                  // * get tx via RPC usually will fetch from block or from pending
                  // * internal tx pool scenarios are handled directly elsewhere
                // transaction = _txStorage.Get(hash);
            }

            return transaction != null;
        }
        
        // TODO: Ensure that nonce is always valid in case of sending own transactions from different nodes.
        public UInt256 ReserveOwnTransactionNonce(Address address)
        {
            lock (_locker)
            {
                if (!_nonces.TryGetValue(address, out var addressNonces))
                {
                    var currentNonce = _stateProvider.GetNonce(address);
                    addressNonces = new AddressNonces(currentNonce);
                    _nonces.TryAdd(address, addressNonces);

                    return currentNonce;
                }

                Nonce incrementedNonce = addressNonces.ReserveNonce();

                return incrementedNonce.Value;
            }
        }

        public void Dispose()
        {
            _ownTimer?.Dispose();
        }

        public event EventHandler<TxEventArgs> NewDiscovered;
        public event EventHandler<TxEventArgs> NewPending;
        public event EventHandler<TxEventArgs> RemovedPending;

        private void Notify(ITxPoolPeer peer, Transaction tx, bool isPriority)
        {
            Metrics.PendingTransactionsSent++;
            peer.SendNewTransaction(tx, isPriority);

            if (_logger.IsTrace) _logger.Trace($"Notified {peer.Enode} about a transaction: {tx.Hash}");
        }

        private void NotifyAllPeers(Transaction tx)
        {
            foreach ((_, ITxPoolPeer peer) in _peers)
            {
                Notify(peer, tx, true);
            }
        }

        private void NotifySelectedPeers(Transaction tx)
        {
            foreach ((_, ITxPoolPeer peer) in _peers)
            {
                if (tx.DeliveredBy == null)
                {
                    Notify(peer, tx, true);
                    continue;
                }

                if (tx.DeliveredBy.Equals(peer.Id))
                {
                    continue;
                }

                if (_peerNotificationThreshold < Random.Value.Next(1, 100))
                {
                    continue;
                }

                Notify(peer, tx, 3 < Random.Value.Next(1, 10));
            }
        }

        private void StoreTx(Transaction tx)
        {
            _txStorage.Add(tx);
            if (_logger.IsTrace) _logger.Trace($"Added a transaction: {tx.Hash}");
        }

        private void OwnTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (_ownTransactions.Count > 0)
            {
                foreach ((_, Transaction tx) in _ownTransactions)
                {
                    NotifyAllPeers(tx);
                }

                // we only reenable the timer if there are any transaction pending
                // otherwise adding own transaction will reenable the timer anyway
                _ownTimer.Enabled = true;
            }
        }

        private class AddressNonces
        {
            private Nonce _currentNonce;

            public ConcurrentDictionary<UInt256, Nonce> Nonces { get; } = new ConcurrentDictionary<UInt256, Nonce>();

            public AddressNonces(UInt256 startNonce)
            {
                _currentNonce = new Nonce(startNonce);
                Nonces.TryAdd(_currentNonce.Value, _currentNonce);
            }

            public Nonce ReserveNonce()
            {
                var nonce = _currentNonce.Increment();
                Interlocked.Exchange(ref _currentNonce, nonce);
                Nonces.TryAdd(nonce.Value, nonce);

                return nonce;
            }
        }

        private class Nonce
        {
            public UInt256 Value { get; }
            public Keccak TransactionHash { get; private set; }

            public Nonce(UInt256 value)
            {
                Value = value;
            }

            public void SetTransactionHash(Keccak transactionHash)
            {
                TransactionHash = transactionHash;
            }

            public Nonce Increment() => new Nonce(Value + 1);
        }
    }
}
