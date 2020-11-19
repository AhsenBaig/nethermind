﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainAuRa : InitializeBlockchain
    {
        private readonly AuRaNethermindApi _api;
        private INethermindApi NethermindApi => _api;
        
        private AuRaSealValidator? _sealValidator;
        private readonly IAuraConfig _auraConfig;
        private BlockProcessorWrapper? _blockProcessorWrapper = null;

        public InitializeBlockchainAuRa(AuRaNethermindApi api) : base(api)
        {
            _api = api;
            _auraConfig = NethermindApi.Config<IAuraConfig>();
        }

        protected override BlockProcessor CreateBlockProcessor()
        {
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.BlockValidator == null) throw new StepDependencyException(nameof(_api.BlockValidator));
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.TransactionProcessor == null) throw new StepDependencyException(nameof(_api.TransactionProcessor));
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.StateProvider == null) throw new StepDependencyException(nameof(_api.StateProvider));
            if (_api.StorageProvider == null) throw new StepDependencyException(nameof(_api.StorageProvider));
            if (_api.TxPool == null) throw new StepDependencyException(nameof(_api.TxPool));
            if (_api.ReceiptStorage == null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
            
            var processingReadOnlyTransactionProcessorSource = new ReadOnlyTxProcessorSource(_api.DbProvider, _api.BlockTree, _api.SpecProvider, _api.LogManager);
            var txPermissionFilterOnlyTxProcessorSource = new ReadOnlyTxProcessorSource(_api.DbProvider, _api.BlockTree, _api.SpecProvider, _api.LogManager);
            ITxFilter? txPermissionFilter = TxFilterBuilders.CreateTxPermissionFilter(_api, txPermissionFilterOnlyTxProcessorSource, _api.StateProvider);
            
            var processor = new AuRaBlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(_api.TransactionProcessor),
                _api.TransactionProcessor,
                _api.DbProvider.StateDb,
                _api.DbProvider.CodeDb,
                _api.StateProvider,
                _api.StorageProvider,
                _api.TxPool,
                _api.ReceiptStorage,
                _api.LogManager,
                _api.BlockTree,
                txPermissionFilter,
                GetGasLimitCalculator());
            
            var auRaValidator = CreateAuRaValidator(processor, processingReadOnlyTransactionProcessorSource);
            processor.AuRaValidator = auRaValidator;
            var reportingValidator = auRaValidator.GetReportingValidator();
            _api.ReportingValidator = reportingValidator;
            if (_sealValidator != null)
            {
                _sealValidator.ReportingValidator = reportingValidator;
            }

            if (_blockProcessorWrapper != null)
            {
                _blockProcessorWrapper.Processor = processor;
            }

            return processor;
        }

        private IAuRaValidator CreateAuRaValidator(IBlockProcessor processor, ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));

            var chainSpecAuRa = _api.ChainSpec.AuRa;
            
            _api.FinalizationManager = new AuRaBlockFinalizationManager(
                _api.BlockTree, 
                _api.ChainLevelInfoRepository, 
                processor, 
                _api.ValidatorStore, 
                new ValidSealerStrategy(), 
                _api.LogManager, 
                chainSpecAuRa.TwoThirdsMajorityTransition);
            
            IAuRaValidator validator = new AuRaValidatorFactory(
                    _api.StateProvider, 
                    _api.AbiEncoder, 
                    _api.TransactionProcessor, 
                    readOnlyTxProcessorSource, 
                    _api.BlockTree, 
                    _api.ReceiptStorage, 
                    _api.ValidatorStore,
                    _api.FinalizationManager,
                    new TxPoolSender(_api.TxPool, new NonceReservingTxSealer(_api.EngineSigner, _api.Timestamper, _api.TxPool)), 
                    _api.TxPool,
                    NethermindApi.Config<IMiningConfig>(),
                    _api.LogManager,
                    _api.EngineSigner,
                    _api.ReportingContractValidatorCache,
                    chainSpecAuRa.PosdaoTransition,
                    false)
                .CreateValidatorProcessor(chainSpecAuRa.Validators, _api.BlockTree.Head?.Header);

            if (validator is IDisposable disposableValidator)
            {
                _api.DisposeStack.Push(disposableValidator);
            }

            return validator;
        }

        private AuRaContractGasLimitOverride? GetGasLimitCalculator()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;
            
            if (blockGasLimitContractTransitions?.Any() == true)
            {
                _api.GasLimitCalculatorCache = new AuRaContractGasLimitOverride.Cache();
                
                AuRaContractGasLimitOverride gasLimitCalculator = new AuRaContractGasLimitOverride(
                    blockGasLimitContractTransitions.Select(blockGasLimitContractTransition =>
                        new BlockGasLimitContract(
                            _api.AbiEncoder,
                            blockGasLimitContractTransition.Value,
                            blockGasLimitContractTransition.Key,
                            new ReadOnlyTxProcessorSource(_api.DbProvider, _api.BlockTree, _api.SpecProvider, _api.LogManager)))
                        .ToArray<IBlockGasLimitContract>(),
                    _api.GasLimitCalculatorCache,
                    _auraConfig.Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract,
                    new TargetAdjustedGasLimitCalculator(_api.SpecProvider, NethermindApi.Config<IMiningConfig>()), 
                    _api.LogManager);
                
                return gasLimitCalculator;
            }

            // do not return target gas limit calculator here - this is used for validation to check if the override should have been used
            return null;
        }

        protected override void InitSealEngine()
        {
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.EthereumEcdsa == null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            
            _api.ValidatorStore = new ValidatorStore(_api.DbProvider.BlockInfosDb);

            ValidSealerStrategy validSealerStrategy = new ValidSealerStrategy();
            AuRaStepCalculator auRaStepCalculator = new AuRaStepCalculator(_api.ChainSpec.AuRa.StepDuration, _api.Timestamper, _api.LogManager);
            _api.SealValidator = _sealValidator = new AuRaSealValidator(_api.ChainSpec.AuRa, auRaStepCalculator, _api.BlockTree, _api.ValidatorStore, validSealerStrategy, _api.EthereumEcdsa, _api.LogManager);
            _api.RewardCalculatorSource = AuRaRewardCalculator.GetSource(_api.ChainSpec.AuRa, _api.AbiEncoder);
            _api.Sealer = new AuRaSealer(_api.BlockTree, _api.ValidatorStore, auRaStepCalculator, _api.EngineSigner, validSealerStrategy, _api.LogManager);
        }

        protected override HeaderValidator CreateHeaderValidator()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            var blockGasLimitContractTransitions = _api.ChainSpec.AuRa.BlockGasLimitContractTransitions;
            return blockGasLimitContractTransitions?.Any() == true
                ? new AuRaHeaderValidator(
                    _api.BlockTree,
                    _api.SealValidator,
                    _api.SpecProvider,
                    _api.LogManager,
                    blockGasLimitContractTransitions.Keys.ToArray())
                : base.CreateHeaderValidator();
        }

        private IComparer<Transaction> CreateTxPoolTxComparer(TxPriorityContract? txPriorityContract, TxPriorityContract.LocalDataSource? localDataSource)
        {
            if (txPriorityContract != null || localDataSource != null)
            {
                ContractDataStore<Address, IContractDataStoreCollection<Address>> whitelistContractDataStore = new ContractDataStoreWithLocalData<Address>(
                    new HashSetContractDataStoreCollection<Address>(),
                    txPriorityContract?.SendersWhitelist,
                    _blockProcessorWrapper,
                    _api.LogManager,
                    localDataSource?.GetWhitelistLocalDataSource() ?? new EmptyLocalDataSource<IEnumerable<Address>>());

                DictionaryContractDataStore<TxPriorityContract.Destination, TxPriorityContract.DestinationSortedListContractDataStoreCollection> prioritiesContractDataStore =
                    new DictionaryContractDataStore<TxPriorityContract.Destination, TxPriorityContract.DestinationSortedListContractDataStoreCollection>(
                        new TxPriorityContract.DestinationSortedListContractDataStoreCollection(),
                        txPriorityContract?.Priorities,
                        _blockProcessorWrapper,
                        _api.LogManager,
                        localDataSource?.GetPrioritiesLocalDataSource());

                _api.DisposeStack.Push(whitelistContractDataStore);
                _api.DisposeStack.Push(prioritiesContractDataStore);
                IComparer<Transaction> txByPermissionComparer = new CompareTxByPermissionOnHead(whitelistContractDataStore, prioritiesContractDataStore, _api.BlockTree);
                
                return CompareTxByGasPrice.Instance
                    .ThenBy(txByPermissionComparer)
                    .ThenBy(CompareTxByTimestamp.Instance)
                    .ThenBy(CompareTxByPoolIndex.Instance)
                    .ThenBy(CompareTxByGasLimit.Instance);
            }
            
            return CreateTxPoolTxComparer();
        }

        protected override TxPool.TxPool CreateTxPool(PersistentTxStorage txStorage)
        {
            _blockProcessorWrapper = new BlockProcessorWrapper();
            
            // This has to be different object than the _processingReadOnlyTransactionProcessorSource as this is in separate thread
            var txPoolReadOnlyTransactionProcessorSource = new ReadOnlyTxProcessorSource(_api.DbProvider, _api.BlockTree, _api.SpecProvider, _api.LogManager);
            var (txPriorityContract, localDataSource) = TxFilterBuilders.CreateTxPrioritySources(_auraConfig, _api, txPoolReadOnlyTransactionProcessorSource!);
            var minGasPricesContractDataStore = TxFilterBuilders.CreateMinGasPricesDataStore(_api, txPriorityContract, localDataSource, _blockProcessorWrapper);

            ITxFilter txPoolFilter = TxFilterBuilders.CreateAuRaTxFilter(
                NethermindApi.Config<IMiningConfig>(),
                _api,
                txPoolReadOnlyTransactionProcessorSource,
                _api.StateProvider!,
                minGasPricesContractDataStore);
            
            return new FilteredTxPool(
                txStorage,
                _api.EthereumEcdsa,
                _api.SpecProvider,
                NethermindApi.Config<ITxPoolConfig>(),
                _api.StateProvider,
                _api.LogManager,
                CreateTxPoolTxComparer(txPriorityContract, localDataSource),
                new TxFilterAdapter(_api.BlockTree, txPoolFilter));
        }

        private class BlockProcessorWrapper : IBlockProcessor
        {
            private AuRaBlockProcessor? _processor;

            public AuRaBlockProcessor? Processor
            {
                get => _processor;
                set
                {
                    if (_processor != null)
                    {
                        _processor.BlockProcessed -= OnBlockProcessed;
                        _processor.TransactionProcessed -= OnTransactionProcessed;
                    }
                    _processor = value;
                    if (_processor != null)
                    {
                        _processor.BlockProcessed += OnBlockProcessed;
                        _processor.TransactionProcessed += OnTransactionProcessed;
                    }
                }
            }

            public Block[] Process(
                Keccak newBranchStateRoot,
                List<Block> suggestedBlocks,
                ProcessingOptions processingOptions,
                IBlockTracer blockTracer) => 
                Processor?.Process(newBranchStateRoot, suggestedBlocks, processingOptions, blockTracer) ?? Array.Empty<Block>();

            public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;

            public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;
            
            private void OnTransactionProcessed(object? sender, TxProcessedEventArgs e)
            {
                TransactionProcessed?.Invoke(sender, e);
            }

            private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
            {
                BlockProcessed?.Invoke(sender, e);
            }
        }
    }
}
