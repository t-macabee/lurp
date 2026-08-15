namespace Lurp.Tests;

public static class MultiProjectFixture
{
    public static IReadOnlyDictionary<string, string> Core { get; } = new Dictionary<string, string>
    {
        ["IBaseReadService.cs"] = """
                                  namespace Core;

                                  public interface IBaseReadService<TResponse, TSearch>
                                  {
                                      TResponse Find(TSearch search);
                                  }
                                  """,
        ["BaseReadService.cs"] = """
                                 namespace Core;

                                 public class BaseReadService<TEntity, TResponse, TSearch> : IBaseReadService<TResponse, TSearch>
                                 {
                                     public virtual TResponse Find(TSearch search) => default!;
                                 }
                                 """
    };

    public static IReadOnlyDictionary<string, string> Services { get; } = new Dictionary<string, string>
    {
        ["UserService.cs"] = """
                             using Core;

                             namespace Services;

                             public class User { }
                             public class UserDto { }
                             public class UserSearch { }

                             public class UserService : BaseReadService<User, UserDto, UserSearch>
                             {
                             }
                             """,
        ["OrderService.cs"] = """
                              using Core;

                              namespace Services;

                              public class Order { }
                              public class OrderDto { }
                              public class OrderSearch { }

                              public class OrderService : BaseReadService<Order, OrderDto, OrderSearch>
                              {
                              }
                              """,
        ["ReportingService.Part1.cs"] = """
                                        using Core;

                                        namespace Services;

                                        public class Report { }
                                        public class ReportDto { }
                                        public class ReportSearch { }

                                        public partial class ReportingService : BaseReadService<Report, ReportDto, ReportSearch>
                                        {
                                            public T Echo<T>(T value) => value;
                                        }
                                        """,
        ["ReportingService.Part2.cs"] = """
                                        using Core;

                                        namespace Services;

                                        public partial class ReportingService
                                        {
                                            public string Format(ReportDto dto) => dto.ToString() ?? "";
                                        }
                                        """,
        ["ServiceRegistration.cs"] = """
                                     using Core;
                                     using Microsoft.Extensions.DependencyInjection;

                                     namespace Services;

                                     public static class ServiceRegistration
                                     {
                                         public static void Register(IServiceCollection services)
                                         {
                                             services.AddScoped<IBaseReadService<UserDto, UserSearch>, UserService>();
                                         }
                                     }

                                     namespace Microsoft.Extensions.DependencyInjection
                                     {
                                         public interface IServiceCollection
                                         {
                                         }

                                         public static class ServiceCollectionServiceExtensions
                                         {
                                             public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
                                                 where TService : class
                                                 where TImplementation : class, TService
                                                 => services;
                                         }
                                     }
                                     """
    };

    public static IReadOnlyDictionary<string, string> App { get; } = new Dictionary<string, string>
    {
        ["Consumer.cs"] = """
                          using Core;
                          using Services;

                          namespace App;

                          public class Consumer
                          {
                              public void UseServices()
                              {
                                  var userSvc = new UserService();
                                  userSvc.Find(new UserSearch());

                                  var orderSvc = new OrderService();
                                  orderSvc.Find(new OrderSearch());

                                  var reportSvc = new ReportingService();
                                  reportSvc.Find(new ReportSearch());
                                  reportSvc.Echo(42);
                                  reportSvc.Format(new ReportDto());
                              }
                          }
                          """
    };

    public static void Seed(IntegrationTestBase t)
    {
        t.CreateProject("Core", Core);
        t.CreateProject("Services", Services, projectReferences: ["Core"]);
        t.CreateProject("App", App, projectReferences: ["Core", "Services"]);
    }
}