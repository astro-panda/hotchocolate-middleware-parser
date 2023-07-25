using HotChocolate.Data.Filters;
using HotChocolate.Data.Sorting;
using HotChocolate.Resolvers;
using HotChocolate.Types.Pagination;
using Humanizer;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace HotChocolateMiddlewareParser;

/// <summary>
/// A parser for the HotChocolate middleware. For when your data source is not sql and things need to be handled manually
/// </summary>
public class HotChocolateMiddlewareParser<T>
{
    /// <param name="dataSource">The source for your data</param>
    /// <param name="resolver">The resolver context from the middleware</param>
    /// <param name="filter">The filter context from the middleware</param>
    /// <param name="sorting">The sorting context from the middleware</param>
    /// <param name="defaultPagingFirst">The default value for paging in the situation where a value is not provided in the paging context</param>
    /// <param name="propertyMapper">An optional property mapper for when the type of your data source does not match the Hot Chocolate type</param>
    public HotChocolateMiddlewareParser(IQueryable<T> dataSource,
                                        IResolverContext resolver,
                                        IFilterContext filter,
                                        ISortingContext sorting,
                                        int defaultPagingFirst,
                                        Dictionary<string, string> propertyMapper = null)
    {
        _propertyMapper = propertyMapper;
        _dataSource = dataSource;
        _resolver = resolver;
        _filter = filter;
        _sorting = sorting;
        _defaultPagingFirst = defaultPagingFirst;
        if (propertyMapper is not null && propertyMapper.Count > 0)
        {
            _propertyMapper = new Dictionary<string, string>();
            foreach (var property in propertyMapper)
                _propertyMapper.Add(property.Key.ToLower(), property.Value);

            _usingPropertyMapper = true;
        }
    }

    private int _defaultPagingFirst { get; set; }
    private Dictionary<string, string> _propertyMapper { get; set; }
    private bool _usingPropertyMapper { get; set; } = false;
    private IQueryable<T> _dataSource { get; set; }
    private IResolverContext _resolver { get; set; }
    private IFilterContext _filter { get; set; }
    private ISortingContext _sorting { get; set; }
    private ParameterExpression _parameter = Expression.Parameter(typeof(T));

    /// <summary>
    /// Filters, sorts, and pages the data source. It marks the filtering and sorting middleware as handled
    /// </summary>
    /// <returns>The filtered, sorted, paged data source</returns>
    public IQueryable<T> Parse()
    {
        HandleFilter();
        HandleSorting();
        HandlePaging();
        return _dataSource;
    }

    public void HandleFilter()
    {
        Expression fullExpression = null;
        var filterDict = _filter.ToDictionary();
        foreach (var filter in filterDict)
        {
            Expression filterExpression = null;
            switch (filter.Key)
            {
                case "or":
                    foreach (var expression in GetFilterExpressions((object[])filter.Value))
                    {
                        if (filterExpression is null)
                            filterExpression = expression;
                        else
                            filterExpression = Expression.Or(filterExpression, expression);
                    }
                    break;
                case "and":
                    foreach (var expression in GetFilterExpressions((object[])filter.Value))
                    {
                        if (filterExpression is null)
                            filterExpression = expression;
                        else
                            filterExpression = Expression.And(filterExpression, expression);
                    }
                    break;
                default:
                    break;
            }
            if (filterExpression is not null)
            {
                if (fullExpression is null)
                    fullExpression = filterExpression;
                else
                    fullExpression = Expression.And(fullExpression, filterExpression);
            }
        }
        var whereLambda = Expression.Lambda<Func<T, bool>>(fullExpression, _parameter);
        _dataSource = _dataSource.Where(whereLambda);
        _filter.Handled(true);
    }

    public void HandleSorting()
    {
        IOrderedQueryable<T> orderedData = null;
        var sortingList = _sorting.ToList();
        if (sortingList.Count > 0)
        {
            foreach (var sorting in sortingList[0])
            {
                MemberExpression property = _getProperty(sorting.Key);

                var keySelector = Expression.Lambda<Func<T, object>>(property, _parameter);

                if (orderedData is null)
                {
                    if ((string)sorting.Value == "DESC")
                        orderedData = _dataSource.OrderByDescending(keySelector);
                    else
                        orderedData = _dataSource.OrderBy(keySelector);
                }
                else
                {
                    if ((string)sorting.Value == "DESC")
                        orderedData = orderedData.ThenByDescending(keySelector);
                    else
                        orderedData = orderedData.ThenBy(keySelector);
                }
            }
            _dataSource = orderedData;
        }
        _sorting.Handled(true);
    }

    public void HandlePaging()
    {
        var pagingArgs = new CursorPagingArguments(
                first: _resolver?.ArgumentValue<int?>("first"),
                after: _resolver?.ArgumentValue<string>("after"),
                last: _resolver?.ArgumentValue<int?>("last"),
                before: _resolver?.ArgumentValue<string>("before")
            );
        int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(pagingArgs.After ?? "")), out int after);

        if (pagingArgs.Last is null)
            _dataSource = _dataSource.Skip(after).Take(pagingArgs.First ?? _defaultPagingFirst);
        else
            _dataSource = _dataSource.Skip(after).Take((int)pagingArgs.Last);
    }

    /// <summary>
    /// Parses the <see cref="IFilterContext"/> from HotChocolate
    /// </summary>
    /// <param name="filters"></param>
    /// <returns>The parsed filter expressions</returns>
    /// <exception cref="KeyNotFoundException">Thrown when you provide a property mapper in the constructor but Hot Chocolate is trying to filter on a property that is not in the property mapper</exception>
    public IEnumerable<Expression> GetFilterExpressions(object[] filters)
    {
        foreach (var α in filters)
        {
            foreach (DictionaryEntry ß in (IDictionary)α)
            {
                MemberExpression property = _getProperty((string)ß.Key);

                foreach (DictionaryEntry entry in (IDictionary)ß.Value)
                {
                    var value = Expression.Constant(entry.Value);
                    Expression expression = _getExpression((string)entry.Key, property, value);
                    if (expression is not null)
                    {
                        yield return expression;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Creates an expression from the property, operation, and the constant value
    /// </summary>
    /// <param name="operation">The operation used in the expression</param>
    /// <param name="property">The <see cref="MemberExpression"/> that represents the property for the expression</param>
    /// <param name="value">The <see cref="ConstantExpression"/> that the property is compared against</param>
    /// <returns>The <see cref="Expression"/> created from the property, operation, and the constant value. Will return null if the operation is not currently supported</returns>
    private Expression _getExpression(string operation, MemberExpression property, ConstantExpression value)
    {
        switch (operation)
        {
            case "eq":
                return Expression.Equal(property, value);
            case "neq":
                return Expression.NotEqual(property, value);
            case "contains":
                MethodInfo containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });
                return Expression.Call(property, containsMethod, value);
            case "endsWith":
                MethodInfo startsWithMethod = typeof(string).GetMethod("StartsWith", new[] { typeof(string) });
                return Expression.Call(property, startsWithMethod, value);
            case "startsWith":
                MethodInfo endsWithMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });
                return Expression.Call(property, endsWithMethod, value);
            case "lt": // Less Than
            case "ngte": // Not Greater Than Or Equal
                return Expression.LessThan(property, value);
            case "lte": // Less Than Or Equal
            case "ngt": // Not Greater Than
                return Expression.LessThanOrEqual(property, value);
            case "gt": // Greater Than
            case "nlte": // Not Less Than Or Equal
                return Expression.GreaterThan(property, value);
            case "gte": // Greater Than Or Equal
            case "nlt":// Not Less Than
                return Expression.GreaterThanOrEqual(property, value);
            default:
                return null;
        }
    }

    /// <summary>
    /// Gets the <see cref="MemberExpression"/> from the property name. Uses the property mapper if it is in use
    /// </summary>
    /// <param name="propName">The <see cref="string"/> value of the property name</param>
    /// <returns>The <see cref="MemberExpression"/> representing the property on the class</returns>
    /// <exception cref="KeyNotFoundException">Throws this when a property mapper is in use but HotChocolate tries to use a property not in the mapper</exception>
    private MemberExpression _getProperty(string propName)
    {
        if (_usingPropertyMapper)
        {
            if (_propertyMapper.TryGetValue(propName.ToLower(), out string invItemPropName))
                return Expression.Property(_parameter, invItemPropName.Pascalize());
            else
                throw new KeyNotFoundException($"You provided a property mapper and HotChocolate tried to use the property " +
                                               $"{propName.Pascalize()} but it was not found in the property mapper.");
        }
        else
            return Expression.Property(_parameter, propName.Pascalize());
    }
}