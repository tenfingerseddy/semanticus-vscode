// Register the pure sharding tests with npm test and the coverage oracle.
// Includes plan-only revision and runtime theory expansion regressions.
// The imported tests inject gate output. They never execute the .NET suite.
import '../../tools/suite-shard.test.mjs';
