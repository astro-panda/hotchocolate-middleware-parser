using HotChocolate.Data.Filters;
using HotChocolate.Data.Sorting;
using HotChocolate.Resolvers;
using HotChocolateMiddlewareParser;
using Moq;

namespace HotChocolateMiddlewareParserTests
{
    public class HotChocolateMiddlewareParserTests
    {
        private HotChocolateMiddlewareParser<Dummy> sut;
        private Mock<IFilterContext> _mockFilterContext = new();
        private Mock<ISortingContext> _mockSortingContext = new();
        private Mock<IResolverContext> _mockResolverContext = new();
        private Mock<IQueryable<Dummy>> _mockData = new();

        private void ResetSut(int defaultPaging = 10, Dictionary<string, string> propertyMapper = null)
        {
            sut = new HotChocolateMiddlewareParser<Dummy>(_mockData.Object,
                                                          _mockResolverContext.Object,
                                                          _mockFilterContext.Object,
                                                          _mockSortingContext.Object,
                                                          defaultPaging,
                                                          propertyMapper);
        }

        [Fact]
        public void WhenNull_TryFromBase64_FailsAndReturns0()
        {
            // Arrange
            ResetSut();

            // Act
            var success = sut.TryFromBase64(null, out int result);

            // Assert
            Assert.False(success);
            Assert.Equal(0, result);
        }

        [Theory]
        [InlineData("MA==", 0)]
        [InlineData("MQ==", 1)]
        [InlineData("Mg==", 2)]
        [InlineData("NDc=", 47)]
        public void TryFromBase64_DecodesCorrectly(string? input, int expected)
        {
            // Arrange
            ResetSut();

            // Act
            var success = sut.TryFromBase64(input, out int result);

            // Assert
            Assert.True(success);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(0, "MA==")]
        [InlineData(1, "MQ==")]
        [InlineData(2, "Mg==")]
        [InlineData(47, "NDc=")]
        public void ToBase64_EncodesCorrectly(int input, string expected)
        {
            // Arrange
            ResetSut();

            // Act
            var result = sut.ToBase64(input);

            // Assert
            Assert.Equal(expected, result);
        }
    }
}