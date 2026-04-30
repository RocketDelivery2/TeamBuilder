using FluentAssertions;
using TeamBuilder.Application.Models;

namespace TeamBuilder.Tests.Application;

public class PaginatedResultTests
{
    [Fact]
    public void TotalPages_ShouldBeOne_WhenCountFitsExactlyInOnePage()
    {
        var result = new PaginatedResult<int> { TotalCount = 10, Page = 1, PageSize = 10 };
        result.TotalPages.Should().Be(1);
    }

    [Fact]
    public void TotalPages_ShouldRoundUp_WhenRemainder()
    {
        var result = new PaginatedResult<int> { TotalCount = 11, Page = 1, PageSize = 10 };
        result.TotalPages.Should().Be(2);
    }

    [Fact]
    public void TotalPages_ShouldBeZero_WhenTotalCountIsZero()
    {
        var result = new PaginatedResult<int> { TotalCount = 0, Page = 1, PageSize = 10 };
        result.TotalPages.Should().Be(0);
    }

    [Fact]
    public void HasPreviousPage_ShouldBeFalse_OnFirstPage()
    {
        var result = new PaginatedResult<int> { TotalCount = 20, Page = 1, PageSize = 10 };
        result.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public void HasPreviousPage_ShouldBeTrue_OnSecondPage()
    {
        var result = new PaginatedResult<int> { TotalCount = 20, Page = 2, PageSize = 10 };
        result.HasPreviousPage.Should().BeTrue();
    }

    [Fact]
    public void HasNextPage_ShouldBeFalse_OnLastPage()
    {
        var result = new PaginatedResult<int> { TotalCount = 20, Page = 2, PageSize = 10 };
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public void HasNextPage_ShouldBeTrue_WhenMorePagesExist()
    {
        var result = new PaginatedResult<int> { TotalCount = 21, Page = 1, PageSize = 10 };
        result.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public void HasNextPage_ShouldBeFalse_WhenTotalCountIsZero()
    {
        var result = new PaginatedResult<int> { TotalCount = 0, Page = 1, PageSize = 10 };
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public void Items_ShouldDefaultToEmpty()
    {
        var result = new PaginatedResult<string>();
        result.Items.Should().NotBeNull();
        result.Items.Should().BeEmpty();
    }
}
